namespace Velo.Tests;

public sealed class QueueVisibilityTests
{
    [Fact]
    public async Task List_CanFilterByOneTaskState()
    {
        var harness = new VeloCliHarness();
        try
        {
            var todoId = await AddTaskAsync(harness, "b15-list-todo");
            var doneId = await AddTaskAsync(harness, "b15-list-done");
            var failedId = await AddTaskAsync(harness, "b15-list-failed");
            MoveTask(harness, doneId, "todo", "done");
            MoveTask(harness, failedId, "todo", "failed");

            var all = await harness.RunAsync("list");
            Assert.Contains(todoId, all.StandardOutput);
            Assert.Contains(doneId, all.StandardOutput);
            Assert.Contains(failedId, all.StandardOutput);

            var done = await harness.RunAsync("list", "--state", "done");
            Assert.Contains(doneId, done.StandardOutput);
            Assert.Contains("b15-list-done", done.StandardOutput);
            Assert.DoesNotContain(todoId, done.StandardOutput);
            Assert.DoesNotContain(failedId, done.StandardOutput);

            var empty = await harness.RunAsync("list", "--state", "running");
            Assert.Equal("No tasks.", empty.StandardOutput.Trim());

            var invalid = await Assert.ThrowsAsync<InvalidOperationException>(
                () => harness.RunAsync("list", "--state", "unknown"));
            Assert.Contains("Usage: velo list [--state <state>]", invalid.Message);

            var repeated = await Assert.ThrowsAsync<InvalidOperationException>(
                () => harness.RunAsync(
                    "list", "--state", "done", "--state", "failed"));
            Assert.Contains("Usage: velo list [--state <state>]", repeated.Message);
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    [Fact]
    public async Task Status_ShowsWorkerStateAndTaskCounts()
    {
        var harness = new VeloCliHarness();
        try
        {
            await harness.StartWorkerAsync();
            var runningWorker = await harness.RunAsync("status");
            Assert.Contains("Velo is running with pid ", runningWorker.StandardOutput);
            Assert.Contains(
                "Tasks: todo=0 running=0 done=0 failed=0 cancelled=0",
                runningWorker.StandardOutput);
            await harness.StopWorkerAsync();

            await AddTaskAsync(harness, "b15-status-todo-one");
            await AddTaskAsync(harness, "b15-status-todo-two");
            var runningId = await AddTaskAsync(harness, "b15-status-running");
            var doneId = await AddTaskAsync(harness, "b15-status-done");
            var failedId = await AddTaskAsync(harness, "b15-status-failed");
            var cancelledId = await AddTaskAsync(harness, "b15-status-cancelled");
            MoveTask(harness, runningId, "todo", "running");
            MoveTask(harness, doneId, "todo", "done");
            MoveTask(harness, failedId, "todo", "failed");
            MoveTask(harness, cancelledId, "todo", "cancelled");

            var stoppedWorker = await harness.RunAsync("status");
            Assert.Contains("Velo is not running.", stoppedWorker.StandardOutput);
            Assert.Contains(
                "Tasks: todo=2 running=1 done=1 failed=1 cancelled=1",
                stoppedWorker.StandardOutput);
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    private static async Task<string> AddTaskAsync(
        VeloCliHarness harness,
        string prompt) =>
        (await harness.RunAsync("add", prompt)).StandardOutput.Trim();

    private static void MoveTask(
        VeloCliHarness harness,
        string id,
        string sourceState,
        string targetState) =>
        File.Move(
            harness.TaskPath(sourceState, id),
            harness.TaskPath(targetState, id));
}
