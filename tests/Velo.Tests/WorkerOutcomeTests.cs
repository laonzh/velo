using System.Diagnostics;

namespace Velo.Tests;

public sealed class WorkerOutcomeTests
{
    [Fact]
    public async Task NonZeroExit_FailsTaskWithLastStderrLine()
    {
        var harness = new VeloCliHarness();
        try
        {
            var taskId = (await harness.RunAsync("add", "b8-failure"))
                .StandardOutput.Trim();

            await harness.StartWorkerAsync();
            await harness.WaitForFileAsync(harness.TaskPath("failed", taskId));
            await harness.StopWorkerAsync();

            var task = await harness.RunAsync("show", taskId);
            Assert.Contains("State: failed", task.StandardOutput);
            Assert.Contains("Error: b8 failure final error", task.StandardOutput);
            Assert.DoesNotContain("Error: b8 failure first diagnostic", task.StandardOutput);

            var log = await harness.RunAsync("logs", taskId);
            Assert.Contains("b8 failure first diagnostic", log.StandardOutput);
            Assert.Contains("b8 failure final error", log.StandardOutput);
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    [Fact]
    public async Task Timeout_FailsTaskAndTerminatesCodexProcessTree()
    {
        var harness = new VeloCliHarness();
        try
        {
            var taskId = (await harness.RunAsync("add", "b8-timeout"))
                .StandardOutput.Trim();

            await harness.StartWorkerAsync(timeout: TimeSpan.FromSeconds(1));
            var parentPid = await harness.WaitForPidAsync("b8-timeout-parent.pid");
            var childPid = await harness.WaitForPidAsync("b8-timeout-child.pid");

            await harness.WaitForFileAsync(harness.TaskPath("failed", taskId));
            await harness.WaitForProcessExitAsync(parentPid);
            await harness.WaitForProcessExitAsync(childPid);
            await harness.StopWorkerAsync();

            var task = await harness.RunAsync("show", taskId);
            Assert.Contains("State: failed", task.StandardOutput);
            Assert.Contains("Error: Timed out after 00:00:01.", task.StandardOutput);
            Assert.False(IsProcessRunning(parentPid));
            Assert.False(IsProcessRunning(childPid));
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
        Assert.All(harness.ObservedProcessIds, pid => Assert.False(IsProcessRunning(pid)));
    }

    [Fact]
    public async Task Retry_FailedTaskClearsErrorAndCanComplete()
    {
        var harness = new VeloCliHarness();
        try
        {
            var taskId = (await harness.RunAsync("add", "b8-retry"))
                .StandardOutput.Trim();

            await harness.StartWorkerAsync();
            await harness.WaitForFileAsync(harness.TaskPath("failed", taskId));
            await harness.StopWorkerAsync();

            var failed = await harness.RunAsync("show", taskId);
            Assert.Contains("State: failed", failed.StandardOutput);
            Assert.Contains("Error: b8 retry transient error", failed.StandardOutput);

            var retry = await harness.RunAsync("retry", taskId);
            Assert.Contains($"Queued: {taskId}", retry.StandardOutput);

            var queued = await harness.RunAsync("show", taskId);
            Assert.Contains("State: todo", queued.StandardOutput);
            Assert.DoesNotContain("Error:", queued.StandardOutput);

            await harness.StartWorkerAsync();
            await harness.WaitForFileAsync(harness.TaskPath("done", taskId));
            await harness.WaitForFileAsync(harness.HomePath("b8-retry.succeeded"));
            await harness.StopWorkerAsync();

            var completed = await harness.RunAsync("show", taskId);
            Assert.Contains("State: done", completed.StandardOutput);
            Assert.DoesNotContain("Error:", completed.StandardOutput);
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
