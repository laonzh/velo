namespace Velo;

/// <summary>
/// Wake-driven FIFO scheduling: starts todo issues, lowest number first, while fewer than maxConcurrency run,
/// and tracks the running ones. Its lock comes before the store's lock.
/// </summary>
internal sealed class Scheduler(Store store, int maxConcurrency, Func<Issue, CancellationToken, Task> execute)
{
    private readonly Lock _gate = new(); // guards _active, _busy, _stopping and scheduling transitions
    private readonly Dictionary<int, ActiveRun> _active = [];
    private readonly HashSet<int> _busy = []; // issues with an action in progress, including across awaits
    private bool _stopping;

    public sealed class ActiveRun
    {
        public CancellationTokenSource Cts { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>Kills the agent and waits until the run is wrapped up.</summary>
        public async Task CancelAsync()
        {
            Cts.Cancel();
            await Task;
        }
    }

    /// <summary>True once <see cref="ShutdownAsync"/> has begun: cancelled runs then end as interrupted.</summary>
    public bool IsStopping
    {
        get { lock (_gate) return _stopping; }
    }

    /// <summary>Starts todo issues (lowest number first) while fewer than maxConcurrency run.</summary>
    public void Wake()
    {
        lock (_gate)
        {
            while (!_stopping && _active.Count < maxConcurrency
                   && store.List().FirstOrDefault(i => i.Status == IssueStatus.Todo && !_busy.Contains(i.Number) && !_active.ContainsKey(i.Number)) is { } next)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                var run = new Run { Number = next.Runs.Count + 1, Status = RunStatus.Running, Message = next.Message, StartedAt = now };
                Issue issue;
                try
                {
                    issue = store.Update(next.Number, i => i with { Status = IssueStatus.Running, Message = null, UpdatedAt = now, Runs = [.. i.Runs, run] });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Wake runs after actions and runs, which must not fail for it: the issue stays todo for the next wake
                    Console.Error.WriteLine($"velo: cannot start {next.Key}: {ex.Message}");
                    return;
                }
                var active = new ActiveRun();
                _active[issue.Number] = active;
                active.Task = Task.Run(() => RunAsync(issue, active.Cts.Token));
            }
        }
    }

    private async Task RunAsync(Issue issue, CancellationToken ct)
    {
        try
        {
            await execute(issue, ct); // never throws
        }
        finally
        {
            lock (_gate) _active.Remove(issue.Number);
            Wake();
        }
    }

    /// <summary>
    /// Excludes other actions and scheduling for this issue until disposed; overlapping actions get 409.
    /// </summary>
    public IDisposable BeginOperation(int number)
    {
        lock (_gate)
        {
            if (!_busy.Add(number)) throw VeloException.Conflict($"VL-{number} has another operation in progress");
            return new Operation(this, number);
        }
    }

    private void EndOperation(int number)
    {
        lock (_gate) _busy.Remove(number);
        Wake();
    }

    private sealed class Operation(Scheduler scheduler, int number) : IDisposable
    {
        private Scheduler? _owner = scheduler;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndOperation(number);
    }

    /// <summary>
    /// The issue's active run, or null after running <paramref name="whenIdle"/> under the lock
    /// (so no run can start in between).
    /// </summary>
    public ActiveRun? ActiveOr(int number, Action whenIdle)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(number, out ActiveRun? active)) return active;
            whenIdle();
            return null;
        }
    }

    /// <summary>Stops every agent and waits for their runs to be wrapped up; nothing is started afterwards.</summary>
    public async Task ShutdownAsync()
    {
        ActiveRun[] runs;
        lock (_gate)
        {
            _stopping = true;
            runs = [.. _active.Values];
        }
        foreach (ActiveRun run in runs) run.Cts.Cancel();
        await Task.WhenAll(runs.Select(r => r.Task));
    }
}
