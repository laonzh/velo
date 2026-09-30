using System.Diagnostics;
using System.Text;

namespace Velo;

internal sealed class AgentRunner(Config config)
{
    private static readonly string[] Arguments =
    [
        "exec",
        "--approve-for-me",
        "--skip-git-repo-check",
        "--color", "never",
        "-"
    ];

    public async Task<RunResult> RunAsync(WorkItem work, CancellationToken cancellationToken)
    {
        var logPath = config.LogFile(work.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        using var log = new StreamWriter(
            new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false))
        { AutoFlush = true };
        var logLock = new object();
        string? error = null;

        void Write(string level, string line)
        {
            lock (logLock)
            {
                log.WriteLine($"[{DateTimeOffset.UtcNow:O}] [{level}] {line}");
                if (level == "ERROR" && !string.IsNullOrWhiteSpace(line)) error ??= line;
            }
        }

        Write("INFO", $"Starting: codex {string.Join(' ', Arguments)}");
        using var process = new Process { StartInfo = CreateStartInfo(work.Workspace) };
        if (!process.Start()) throw new InvalidOperationException("Failed to start codex.");

        var outputTask = PumpAsync(process.StandardOutput, line => Write("INFO", line));
        var errorTask = PumpAsync(process.StandardError, line => Write("ERROR", line));

        try
        {
            await process.StandardInput.WriteAsync(work.Prompt).WaitAsync(cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
            return new RunResult(process.ExitCode, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outputTask, errorTask);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string workspace)
    {
        var executable = ResolveCodex();
        var isCommandScript = OperatingSystem.IsWindows()
            && (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase));

        var startInfo = new ProcessStartInfo
        {
            FileName = isCommandScript
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : executable,
            WorkingDirectory = workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        if (isCommandScript)
        {
            startInfo.Arguments =
                $"/d /s /c \"chcp 65001 >nul & call \"{executable}\" {string.Join(' ', Arguments)}\"";
        }
        else
        {
            foreach (var argument in Arguments) startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static string ResolveCodex()
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, "codex" + extension.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
            }
        }
        return "codex";
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> write)
    {
        while (await reader.ReadLineAsync() is { } line) write(line);
    }
}

internal sealed record RunResult(int ExitCode, string? Error)
{
    public bool Succeeded => ExitCode == 0;
}


