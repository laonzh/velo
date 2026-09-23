using System.Diagnostics;

namespace Velo.Tests;

public sealed class ControlledStopTests
{
    [Fact]
    public async Task ControlledStop_RequeuesInterruptedTask_AndPreservesCancelledTask()
    {
        var harness = new VeloCliHarness();
        try
        {
            var taskId = (await harness.RunAsync("add", "b4-requeue")).StandardOutput.Trim();
            Assert.NotEmpty(taskId);

            await harness.StartWorkerAsync();
            await harness.WaitForFileAsync(harness.TaskPath("running", taskId));
            var interruptedPid = await harness.WaitForPidAsync("requeue.pid");

            await harness.StopWorkerAsync();
            await harness.WaitForProcessExitAsync(interruptedPid);

            var stoppedTask = await harness.RunAsync("show", taskId);
            Assert.Contains("State: todo", stoppedTask.StandardOutput);
            Assert.Contains(
                "Returned 1 interrupted task(s) to todo.",
                await File.ReadAllTextAsync(harness.VeloLogPath));

            await harness.StartWorkerAsync();
            await harness.WaitForFileAsync(harness.TaskPath("done", taskId));
            await harness.WaitForFileAsync(harness.HomePath("requeue.second"));
            await harness.StopWorkerAsync();

            var completedTask = await harness.RunAsync("show", taskId);
            Assert.Contains("State: done", completedTask.StandardOutput);

            var cancelledId = (await harness.RunAsync("add", "b4-cancel"))
                .StandardOutput.Trim();
            Assert.NotEmpty(cancelledId);

            await harness.StartWorkerAsync();
            await harness.WaitForFileAsync(harness.TaskPath("running", cancelledId));
            var cancelledPid = await harness.WaitForPidAsync("cancel.pid");

            await harness.RunAsync("cancel", cancelledId);
            await harness.StopWorkerAsync();
            await harness.WaitForProcessExitAsync(cancelledPid);

            var cancelledTask = await harness.RunAsync("show", cancelledId);
            Assert.Contains("State: cancelled", cancelledTask.StandardOutput);
            Assert.False(File.Exists(harness.TaskPath("todo", cancelledId)));
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
        Assert.All(harness.ObservedProcessIds, pid => Assert.False(IsProcessRunning(pid)));
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
