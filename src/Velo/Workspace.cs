using System.Diagnostics;
using System.Text;

namespace Velo;

internal sealed class Workspace(Config config)
{
    public async Task<string> CreateAsync(string id, string source)
    {
        var repository = Path.GetFullPath(
            (await Git.RunAsync(source, "rev-parse", "--show-toplevel")).Trim());
        var workspace = Path.GetFullPath(config.WorkspacePath(id));
        await Git.RunAsync(repository, "worktree", "add", "-b", $"velo/{id}", workspace, "HEAD");
        return workspace;
    }

    public static string Key(string path)
    {
        var normalized = Normalize(path);
        for (var directory = new DirectoryInfo(normalized);
             directory is not null;
             directory = directory.Parent)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return Normalize(directory.FullName);
        }
        return normalized;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

internal static class Git
{
    public static async Task<string> RunAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(error.Length == 0
                ? $"git exited with code {process.ExitCode}."
                : error);
        }
        return output;
    }
}
