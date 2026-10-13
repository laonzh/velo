using System.Diagnostics;

namespace Velo.Tests;

// Git helpers for tests that need a real repository.
internal static class TestGit
{
    // Creates a repository with one commit, so that worktrees have a HEAD to start from.
    public static async Task InitRepositoryAsync(string path)
    {
        await RunAsync(path, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "source");
        await RunAsync(path, "add", ".");
        await RunAsync(path, "-c", "user.email=velo@example.com", "-c", "user.name=Velo", "commit", "-m", "init");
    }

    public static async Task<string> RunAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(await error);
        return (await output).Trim();
    }
}
