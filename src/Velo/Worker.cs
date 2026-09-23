namespace Velo;

public sealed class Worker(TaskStore store, int concurrency, TimeSpan timeout)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly StringComparer WorkspaceComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        store.Initialize();
        var recovered = store.RecoverRunning();
        var running = new Dictionary<string, (string LockKey, Task Run)>();
        VeloPaths.WriteLog("INFO", $"Worker started (concurrency={concurrency}, timeout={timeout:c}).");
        if (recovered > 0)
            VeloPaths.WriteLog("INFO", $"Recovered {recovered} running task(s) to todo.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RemoveCompletedAsync(running);

                foreach (var task in store.Pending(int.MaxValue))
                {
                    if (running.Count >= concurrency) break;
                    var lockKey = WorkspaceLockKey(task.Item.WorkspacePath);
                    if (running.Values.Any(run =>
                        WorkspaceComparer.Equals(run.LockKey, lockKey))) continue;
                    if (!store.Claim(task.Id)) continue;

                    var claimed = store.Get(task.Id)!;
                    VeloPaths.WriteLog("INFO", $"Task {task.Id} started.");
                    running.Add(task.Id, (lockKey, RunTaskAsync(claimed, cancellationToken)));
                }

                if (running.Count == 0)
                {
                    await Task.Delay(PollInterval, cancellationToken);
                }
                else
                {
                    await Task.WhenAny(
                        Task.WhenAny(running.Values.Select(run => run.Run)),
                        Task.Delay(PollInterval, cancellationToken));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(running.Values.Select(run => run.Run));
            var requeued = store.RecoverRunning();
            if (requeued > 0)
                VeloPaths.WriteLog("INFO", $"Returned {requeued} interrupted task(s) to todo.");
            VeloPaths.WriteLog("INFO", "Worker stopped.");
        }
    }

    private async Task RunTaskAsync(TaskEntry task, CancellationToken daemonToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            daemonToken,
            timeoutCancellation.Token);
        var runTask = CodexRunner.RunAsync(task, runCancellation.Token);

        try
        {
            while (!runTask.IsCompleted)
            {
                await Task.WhenAny(runTask, Task.Delay(PollInterval, CancellationToken.None));
                if (runTask.IsCompleted) break;

                if (store.Get(task.Id)?.State == TaskState.Cancelled)
                {
                    runCancellation.Cancel();
                    break;
                }
            }

            var result = await runTask;
            if (store.Get(task.Id)?.State == TaskState.Cancelled) return;

            if (result.Success)
            {
                store.Complete(task.Id);
                VeloPaths.WriteLog("INFO", $"Task {task.Id} completed.");
            }
            else
            {
                var error = result.Error ?? $"Codex exited with code {result.ExitCode}.";
                store.Fail(task.Id, error);
                VeloPaths.WriteLog("ERROR", $"Task {task.Id} failed: {error}");
            }
        }
        catch (OperationCanceledException)
        {
            if (daemonToken.IsCancellationRequested) return;
            if (store.Get(task.Id)?.State == TaskState.Cancelled) return;
            if (timeoutCancellation.IsCancellationRequested)
            {
                var error = $"Timed out after {timeout:c}.";
                store.Fail(task.Id, error);
                VeloPaths.WriteLog("ERROR", $"Task {task.Id} failed: {error}");
            }
        }
        catch (Exception ex)
        {
            if (!daemonToken.IsCancellationRequested
                && store.Get(task.Id)?.State == TaskState.Running)
            {
                store.Fail(task.Id, ex.Message);
                VeloPaths.WriteLog("ERROR", $"Task {task.Id} failed: {ex.Message}");
            }
        }
    }

    private static string WorkspaceLockKey(string path)
    {
        var workspace = NormalizeWorkspace(path);
        for (var directory = new DirectoryInfo(workspace);
             directory is not null;
             directory = directory.Parent)
        {
            var gitEntry = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                return NormalizeWorkspace(directory.FullName);
        }
        return workspace;
    }

    private static string NormalizeWorkspace(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static async Task RemoveCompletedAsync(
        Dictionary<string, (string LockKey, Task Run)> running)
    {
        foreach (var (id, run) in running.Where(pair => pair.Value.Run.IsCompleted).ToArray())
        {
            await run.Run;
            running.Remove(id);
        }
    }
}
