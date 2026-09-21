using System.Diagnostics;
using System.Text;
using Velo.Abstractions;

namespace Velo.Common;

public static class Utils
{
    public static string ResolveExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentNullException(nameof(executable));
        if (File.Exists(executable)) return executable;

        var pathStr = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathStr)) return executable;
        var paths = pathStr.Split(Path.PathSeparator);
        string[] exts = [];
        if (OperatingSystem.IsWindows())
        {
            var extStr = Environment.GetEnvironmentVariable("PATHEXT");
            if (string.IsNullOrWhiteSpace(extStr))
            {
                exts = [".exe", ".cmd", ".bat"];
            }
            else
            {
                exts = extStr.Split(Path.PathSeparator);
            }
        }

        foreach (var path in paths)
        {
            var fullPath = Path.Combine(path, executable);
            if (File.Exists(fullPath)) return fullPath;
            foreach (var ext in exts)
            {
                var fullPathWithExt = fullPath + ext;
                if (File.Exists(fullPathWithExt)) return fullPathWithExt;
            }
        }
        return executable;
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDir = null,
        ILogWriter? log = null,
        CancellationToken cancellationToken = default)
    {
        var executable = Utils.ResolveExecutable(fileName);
        var startInfo = new ProcessStartInfo()
        {
            FileName = executable,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8
        };

        if (args != null && args.Count > 0)
        {
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg.Trim());
            }
        }

        using var process = new Process() { StartInfo = startInfo };
        if (!process.Start())
        {
            log?.Write("ERROR", "Failed to start process");
            return new(false, string.Empty, "failed to start process");
        }

        static async Task PumpAsync(StreamReader reader, Action<string> onData, CancellationToken cancellationToken = default)
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                onData(line);
            }
        }

        try
        {
            process.StandardInput.Close();
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            var outputTask = PumpAsync(process.StandardOutput, (line) => { outputBuilder.AppendLine(line); log?.Write("INFO", line); }, cancellationToken);
            var errorTask = PumpAsync(process.StandardError, (line) => { errorBuilder.AppendLine(line); log?.Write("ERROR", line); }, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return new(process.ExitCode == 0, outputBuilder.ToString(), errorBuilder.ToString());
        }
        catch (Exception ex)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                    process.WaitForExit(5000);
                }
                catch { }
            }
            log?.Write("ERROR", $"Failed to run process: {ex.Message}");
            return new(false, string.Empty, ex.Message);
        }
    }
}