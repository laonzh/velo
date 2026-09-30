namespace Velo;

internal sealed class Orchestrator(
    WorkQueue queue,
    Func<WorkItem, CancellationToken, Task<RunResult>> runAgent,
    Config config,
    int concurrency)
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (concurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(concurrency), "Concurrency must be at least 1.");

        using var runLock = FileLock.TryOpenExclusive(config.RunLock)
            ?? throw new InvalidOperationException("Another velo run is already active for this VELO_HOME.");

        var interrupted = queue.RecoverRunning();
        if (interrupted > 0)
            Console.WriteLine($"Recovered {interrupted} interrupted work item(s) to pending.");

        var active = new Dictionary<string, Task>(PathComparer);
        while (!cancellationToken.IsCancellationRequested)
        {
            await RemoveCompletedAsync(active);
            if (active.Count < concurrency)
            {
                foreach (var work in queue.Pending())
                {
                    if (active.Count >= concurrency) break;

                    var workspaceKey = Workspace.Key(work.Workspace);
                    if (active.ContainsKey(workspaceKey)) continue;
                    if (!queue.TryClaim(work.Id)) continue;

                    Console.WriteLine($"running {work.Id}: {work.Summary()}");
                    active.Add(workspaceKey, RunWorkAsync(work, cancellationToken));
                }
            }

            if (active.Count == 0) break;

            await Task.WhenAny(
                Task.WhenAny(active.Values),
                Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken));
        }

        await Task.WhenAll(active.Values);
        queue.RecoverRunning();
    }

    private async Task RunWorkAsync(WorkItem work, CancellationToken cancellationToken)
    {
        try
        {
            var result = await runAgent(work, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            if (result.Succeeded)
            {
                queue.TrySucceed(work.Id);
                Console.WriteLine($"succeeded {work.Id}");
            }
            else
            {
                var error = result.Error ?? $"Codex exited with code {result.ExitCode}.";
                queue.TryFail(work.Id, error);
                Console.WriteLine($"failed {work.Id}: {error}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                queue.TryFail(work.Id, ex.Message);
                Console.WriteLine($"failed {work.Id}: {ex.Message}");
            }
        }
    }

    private static async Task RemoveCompletedAsync(Dictionary<string, Task> active)
    {
        foreach (var (workspace, task) in active.Where(pair => pair.Value.IsCompleted).ToArray())
        {
            await task;
            active.Remove(workspace);
        }
    }
}
