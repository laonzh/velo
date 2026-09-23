using System.Diagnostics;

namespace Velo.Tests;

public sealed class TaskRemovalTests
{
    [Theory]
    [InlineData("done")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task Remove_DeletesTerminalTaskAndLogButPreservesOrdinaryWorkspace(
        string terminalState)
    {
        var harness = new VeloCliHarness();
        try
        {
            var workspace = Path.Combine(harness.RootPath, "ordinary-workspace");
            Directory.CreateDirectory(workspace);
            var sentinel = Path.Combine(workspace, "keep.txt");
            await File.WriteAllTextAsync(sentinel, "keep");

            var taskId = (await harness.RunAsync(
                workspace,
                ["add", $"b16-ordinary-{terminalState}"]))
                .StandardOutput.Trim();
            MoveTask(harness, taskId, "todo", terminalState);
            var logPath = harness.HomePath(Path.Combine("logs", taskId + ".log"));
            await File.WriteAllTextAsync(logPath, "task log");

            var removed = await harness.RunAsync("remove", taskId);

            Assert.Contains($"Removed task: {taskId}", removed.StandardOutput);
            Assert.False(File.Exists(harness.TaskPath(terminalState, taskId)));
            Assert.False(File.Exists(logPath));
            Assert.True(Directory.Exists(workspace));
            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    [Theory]
    [InlineData("todo")]
    [InlineData("running")]
    public async Task Remove_RejectsNonTerminalTaskWithoutDeletingAnything(string state)
    {
        var harness = new VeloCliHarness();
        try
        {
            var taskId = (await harness.RunAsync("add", $"b16-{state}"))
                .StandardOutput.Trim();
            if (state != "todo") MoveTask(harness, taskId, "todo", state);
            var logPath = harness.HomePath(Path.Combine("logs", taskId + ".log"));
            await File.WriteAllTextAsync(logPath, "task log");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => harness.RunAsync("remove", taskId));

            Assert.Contains($"Task {taskId} cannot be removed from {state}.", error.Message);
            Assert.True(File.Exists(harness.TaskPath(state, taskId)));
            Assert.True(File.Exists(logPath));
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    [Fact]
    public async Task Remove_RejectsDirtyManagedWorktreeAndPreservesAllTaskData()
    {
        var harness = new VeloCliHarness();
        try
        {
            var repository = await CreateRepositoryAsync(harness.RootPath, "dirty-repository");
            var taskId = (await harness.RunAsync(
                repository,
                ["add", "--worktree", "b16-dirty-worktree"]))
                .StandardOutput.Trim();
            MoveTask(harness, taskId, "todo", "done");

            var worktreeRoot = Path.Combine(harness.VeloHome, "workspaces", taskId);
            var untrackedFile = Path.Combine(worktreeRoot, "uncommitted.txt");
            await File.WriteAllTextAsync(untrackedFile, "valuable result");
            var logPath = harness.HomePath(Path.Combine("logs", taskId + ".log"));
            await File.WriteAllTextAsync(logPath, "task log");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => harness.RunAsync("remove", taskId));

            Assert.Contains(
                $"Managed worktree has uncommitted changes: {worktreeRoot}",
                error.Message);
            Assert.True(File.Exists(harness.TaskPath("done", taskId)));
            Assert.True(File.Exists(logPath));
            Assert.True(Directory.Exists(worktreeRoot));
            Assert.Equal("valuable result", await File.ReadAllTextAsync(untrackedFile));
            Assert.Contains(
                NormalizePath(worktreeRoot),
                NormalizePaths(await RunGitAsync(
                    repository, "worktree", "list", "--porcelain")));
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    [Fact]
    public async Task Remove_DeletesCleanManagedWorktreeAndPreservesBranch()
    {
        var harness = new VeloCliHarness();
        try
        {
            var repository = await CreateRepositoryAsync(harness.RootPath, "clean-repository");
            var taskId = (await harness.RunAsync(
                repository,
                ["add", "--worktree", "b16-clean-worktree"]))
                .StandardOutput.Trim();
            MoveTask(harness, taskId, "todo", "done");

            var worktreeRoot = Path.Combine(harness.VeloHome, "workspaces", taskId);
            var logPath = harness.HomePath(Path.Combine("logs", taskId + ".log"));
            await File.WriteAllTextAsync(logPath, "task log");

            var removed = await harness.RunAsync("remove", taskId);

            Assert.Contains($"Removed task: {taskId}", removed.StandardOutput);
            Assert.Contains($"Kept branch: velo/{taskId}", removed.StandardOutput);
            Assert.False(File.Exists(harness.TaskPath("done", taskId)));
            Assert.False(File.Exists(logPath));
            Assert.False(Directory.Exists(worktreeRoot));
            Assert.Equal(
                $"velo/{taskId}",
                (await RunGitAsync(
                    repository,
                    "branch", "--list", $"velo/{taskId}", "--format=%(refname:short)"))
                    .Trim());
            Assert.DoesNotContain(
                NormalizePath(worktreeRoot),
                NormalizePaths(await RunGitAsync(
                    repository, "worktree", "list", "--porcelain")));
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private static string NormalizePaths(string value) => value.Replace('\\', '/');

    private static void MoveTask(
        VeloCliHarness harness,
        string id,
        string sourceState,
        string targetState) =>
        File.Move(
            harness.TaskPath(sourceState, id),
            harness.TaskPath(targetState, id));

    private static async Task<string> CreateRepositoryAsync(string rootPath, string name)
    {
        var repository = Path.Combine(rootPath, name);
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "committed");
        await RunGitAsync(repository, "init");
        await RunGitAsync(repository, "add", ".");
        await RunGitAsync(
            repository,
            "-c", "user.name=Velo Tests",
            "-c", "user.email=velo-tests@example.invalid",
            "commit", "-m", "initial");
        return repository;
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
