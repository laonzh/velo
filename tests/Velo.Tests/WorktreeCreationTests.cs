using System.Diagnostics;

namespace Velo.Tests;

public sealed class WorktreeCreationTests
{
    [Fact]
    public async Task AddWorktree_CreatesIsolatedBranchAndQueuesMatchingSubdirectory()
    {
        var harness = new VeloCliHarness();
        try
        {
            var repository = Path.Combine(harness.RootPath, "worktree-repository");
            var sourceWorkspace = Path.Combine(repository, "src", "component");
            Directory.CreateDirectory(sourceWorkspace);

            var trackedFile = Path.Combine(sourceWorkspace, "tracked.txt");
            await File.WriteAllTextAsync(trackedFile, "committed");
            await RunGitAsync(repository, "init");
            await RunGitAsync(repository, "add", ".");
            await RunGitAsync(
                repository,
                "-c", "user.name=Velo Tests",
                "-c", "user.email=velo-tests@example.invalid",
                "commit", "-m", "initial");

            var sourceHead = (await RunGitAsync(repository, "rev-parse", "HEAD")).Trim();
            var sourceBranch = (await RunGitAsync(
                repository,
                "branch", "--show-current")).Trim();

            await File.WriteAllTextAsync(trackedFile, "uncommitted change");
            await File.WriteAllTextAsync(
                Path.Combine(sourceWorkspace, "untracked.txt"),
                "source only");

            var taskId = (await harness.RunAsync(
                sourceWorkspace,
                ["add", "--worktree", "b9-worktree"])).StandardOutput.Trim();

            var worktreeRoot = Path.Combine(harness.VeloHome, "workspaces", taskId);
            var taskWorkspace = Path.Combine(worktreeRoot, "src", "component");
            var task = await harness.RunAsync("show", taskId);

            Assert.Contains("State: todo", task.StandardOutput);
            Assert.Contains("Prompt: b9-worktree", task.StandardOutput);
            Assert.Contains($"Workspace: {Path.GetFullPath(taskWorkspace)}", task.StandardOutput);
            Assert.True(File.Exists(harness.TaskPath("todo", taskId)));

            Assert.Equal(
                $"velo/{taskId}",
                (await RunGitAsync(worktreeRoot, "branch", "--show-current")).Trim());
            Assert.Equal(
                sourceHead,
                (await RunGitAsync(worktreeRoot, "rev-parse", "HEAD")).Trim());
            Assert.Equal("committed", await File.ReadAllTextAsync(
                Path.Combine(taskWorkspace, "tracked.txt")));
            Assert.False(File.Exists(Path.Combine(taskWorkspace, "untracked.txt")));

            Assert.Equal(
                sourceBranch,
                (await RunGitAsync(repository, "branch", "--show-current")).Trim());
            Assert.Equal("uncommitted change", await File.ReadAllTextAsync(trackedFile));
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    [Fact]
    public async Task Worker_CompletesTaskInsideManagedWorktreeWithoutChangingSourceCheckout()
    {
        var harness = new VeloCliHarness();
        try
        {
            var repository = Path.Combine(harness.RootPath, "b12-repository");
            var sourceWorkspace = Path.Combine(repository, "src", "component");
            Directory.CreateDirectory(sourceWorkspace);

            var trackedFile = Path.Combine(sourceWorkspace, "tracked.txt");
            await File.WriteAllTextAsync(trackedFile, "committed source");
            await RunGitAsync(repository, "init");
            await RunGitAsync(repository, "add", ".");
            await RunGitAsync(
                repository,
                "-c", "user.name=Velo Tests",
                "-c", "user.email=velo-tests@example.invalid",
                "commit", "-m", "initial");

            var sourceHead = (await RunGitAsync(repository, "rev-parse", "HEAD")).Trim();
            var sourceBranch = (await RunGitAsync(
                repository,
                "branch", "--show-current")).Trim();

            var taskId = (await harness.RunAsync(
                sourceWorkspace,
                ["add", "--worktree", "b12-worktree-execution"]))
                .StandardOutput.Trim();
            var worktreeRoot = Path.Combine(harness.VeloHome, "workspaces", taskId);
            var taskWorkspace = Path.Combine(worktreeRoot, "src", "component");
            var worktreeOutput = Path.Combine(taskWorkspace, "b12-codex-output.txt");

            await harness.StartWorkerAsync();
            await harness.WaitForFileAsync(harness.TaskPath("done", taskId));
            await harness.StopWorkerAsync();

            Assert.Equal("created by fake Codex", await File.ReadAllTextAsync(worktreeOutput));
            Assert.False(File.Exists(Path.Combine(sourceWorkspace, "b12-codex-output.txt")));
            Assert.Equal("committed source", await File.ReadAllTextAsync(trackedFile));

            var task = await harness.RunAsync("show", taskId);
            Assert.Contains("State: done", task.StandardOutput);
            Assert.Contains($"Workspace: {Path.GetFullPath(taskWorkspace)}", task.StandardOutput);

            Assert.True(Directory.Exists(worktreeRoot));
            Assert.Equal(
                $"velo/{taskId}",
                (await RunGitAsync(worktreeRoot, "branch", "--show-current")).Trim());
            Assert.Equal(
                "?? src/component/b12-codex-output.txt",
                (await RunGitAsync(worktreeRoot, "status", "--short")).Trim());

            Assert.Equal(sourceBranch, (await RunGitAsync(
                repository,
                "branch", "--show-current")).Trim());
            Assert.Equal(sourceHead, (await RunGitAsync(repository, "rev-parse", "HEAD")).Trim());
            Assert.Equal(string.Empty, (await RunGitAsync(
                repository,
                "status", "--short")).Trim());
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        params string[] arguments)
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
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"git {string.Join(' ', arguments)} did not exit within 20 seconds.");
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} exited with {process.ExitCode}." +
                $"{Environment.NewLine}stdout: {output}" +
                $"{Environment.NewLine}stderr: {error}");
        }
        return output;
    }
}
