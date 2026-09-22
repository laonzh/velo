using System.Diagnostics;
using System.Text;

namespace Velo;

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string standardInput,
        string logPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        using var log = new StreamWriter(
            new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false))
        { AutoFlush = true };
        var logLock = new object();
        string? lastError = null;

        void Write(string level, string line)
        {
            lock (logLock)
            {
                log.WriteLine($"[{DateTimeOffset.UtcNow:O}] [{level}] {line}");
                if (level == "ERROR" && !string.IsNullOrWhiteSpace(line)) lastError = line;
            }
        }

        var startInfo = CreateStartInfo(fileName, arguments, workingDirectory);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Failed to start {fileName}.");

        var outputTask = PumpAsync(process.StandardOutput, line => Write("INFO", line));
        var errorTask = PumpAsync(process.StandardError, line => Write("ERROR", line));
        await process.StandardInput.WriteAsync(standardInput);
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
            return new ProcessResult(process.ExitCode, lastError);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outputTask, errorTask);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var executable = ResolveExecutable(fileName);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static string ResolveExecutable(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName) && File.Exists(fileName)) return fileName;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, fileName + extension.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
            }
        }
        return fileName;
    }


    private static async Task PumpAsync(StreamReader reader, Action<string> write)
    {
        while (await reader.ReadLineAsync() is { } line) write(line);
    }
}

public static class CodexRunner
{
    private static readonly string[] Arguments =
    [
        "exec",
        "--dangerously-bypass-approvals-and-sandbox",
        "--color", "never",
        "-"
    ];

    public static Task<ProcessResult> RunAsync(
        TaskEntry task,
        CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(
            "codex",
            Arguments,
            task.Item.WorkspacePath,
            task.Item.Title,
            VeloPaths.LogFile(task.Id),
            cancellationToken);
}


