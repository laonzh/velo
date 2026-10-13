namespace Velo;

internal sealed class Orchestrator(
    WorkQueue queue,
    Func<WorkItem, CancellationToken, Task<RunResult>> runAgent,
    Config config,
    int concurrency,
    bool exitWhenIdle = true,
    Action? onStarted = null,
    RefreshSignal? refresh = null)
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly TimeSpan IdleTick = TimeSpan.FromMilliseconds(100);
    private const int IdleTicks = 10;
    private readonly RefreshSignal _refresh = refresh ?? new();

    public async Task RunAsync(CancellationToken cancellationToken, FileStream? runLock = null)
    {
        if (concurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(concurrency), "Concurrency must be at least 1.");

        using var acquiredRunLock = runLock is null
            ? FileLock.TryOpenExclusive(config.RunLock)
                ?? throw new InvalidOperationException("Another velo run is already active for this VELO_HOME.")
            : null;
        onStarted?.Invoke();

        var interrupted = queue.RecoverRunning();
        if (interrupted > 0)
            Console.WriteLine($"Recovered {interrupted} interrupted work item(s) to pending.");

        // Cancelled on every exit path, so no agent can outlive the run lock.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new Dictionary<string, Run>(PathComparer);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await RemoveCompletedAsync(active);
                if (active.Count > 0)
                {
                    foreach (var id in queue.CancelRequests())
                    {
                        foreach (var run in active.Values.Where(run => run.WorkId == id))
                            run.Cancel.Cancel();
                    }
                }

                if (active.Count < concurrency)
                {
                    foreach (var work in queue.Pending())
                    {
                        if (active.Count >= concurrency) break;

                        var workspaceKey = Workspace.Key(work.Workspace);
                        if (active.ContainsKey(workspaceKey)) continue;
                        if (!queue.TryClaim(work.Id)) continue;

                        Console.WriteLine($"running {work.Id}: {work.Summary()}");
                        var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        active.Add(workspaceKey, new Run(
                            work.Id,
                            RunWorkAsync(work, stop.Token, cancel),
                            cancel));
                    }
                }

                if (active.Count == 0)
                {
                    if (exitWhenIdle) break;

                    // Watch mode: nothing is running, so wait for new work without spinning.
                    // A refresh request ends the wait early.
                    for (var tick = 0; tick < IdleTicks && !_refresh.TryConsume(); tick++)
                        await Task.WhenAny(Task.Delay(IdleTick, stop.Token));
                    continue;
                }

                await Task.WhenAny(
                    Task.WhenAny(active.Values.Select(run => run.Task)),
                    Task.Delay(TimeSpan.FromMilliseconds(100), stop.Token));
            }
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(active.Values.Select(run => run.Task));
            foreach (var run in active.Values) run.Cancel.Dispose();
            queue.RecoverRunning();
        }
    }

    // Never throws: a failure to record the outcome is reported, so one bad write cannot
    // fault the task that RemoveCompletedAsync awaits.
    private async Task RunWorkAsync(
        WorkItem work,
        CancellationToken stopToken,
        CancellationTokenSource cancel)
    {
        string error;
        int? exitCode;
        try
        {
            var result = await runAgent(work, cancel.Token);
            if (stopToken.IsCancellationRequested) return;

            if (result.Succeeded)
            {
                Record(work.Id, () => queue.TrySucceed(work.Id, result.ExitCode), $"succeeded {work.Id}");
                return;
            }
            error = result.Error ?? $"Codex exited with code {result.ExitCode}.";
            exitCode = result.ExitCode;
        }
        catch (Exception) when (stopToken.IsCancellationRequested)
        {
            // Left running on purpose: the next start returns it to pending or applies a cancel.
            return;
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
            error = WorkQueue.CancelledMessage;
            exitCode = null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            exitCode = null;
        }

        Record(work.Id, () => queue.TryFail(work.Id, error, exitCode), $"failed {work.Id}: {error}");
    }

    private static void Record(string id, Action transition, string message)
    {
        try
        {
            transition();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"could not record {id}: {ex.Message}");
            return;
        }
        Console.WriteLine(message);
    }

    private static async Task RemoveCompletedAsync(Dictionary<string, Run> active)
    {
        foreach (var (workspace, run) in active.Where(pair => pair.Value.Task.IsCompleted).ToArray())
        {
            await run.Task;
            run.Cancel.Dispose();
            active.Remove(workspace);
        }
    }

    private sealed record Run(string WorkId, Task Task, CancellationTokenSource Cancel);
}
