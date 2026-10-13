using System.Text.Json;

namespace Velo;

internal enum WorkChange { Done, NotFound, NotPending, NotReviewable, Running }

internal enum CancelResult { Cancelled, Requested, NotFound, NotCancellable }

internal sealed class WorkQueue(Config config)
{
    internal const string CancelledMessage = "Cancelled by user.";
    private const string InterruptedMessage = "Interrupted before completion.";

    private readonly HashSet<string> _unreadablePaths = [];

    internal static string NewId() =>
        $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..23];

    public WorkItem Add(string prompt, string workspace) => Add(NewId(), prompt, workspace);

    internal WorkItem Add(string id, string prompt, string workspace, Workspace.Worktree? worktree = null)
    {
        ValidateId(id);
        ValidatePrompt(prompt);
        if (!Directory.Exists(workspace))
            throw new DirectoryNotFoundException($"Workspace does not exist: {workspace}");

        var item = new WorkItem(
            id,
            WorkState.Pending,
            prompt.Trim(),
            Path.GetFullPath(workspace),
            DateTimeOffset.UtcNow,
            Repository: worktree?.Repository,
            BaseCommit: worktree?.BaseCommit);
        return WithLock(() =>
        {
            Write(config.WorkFile(id), item, overwrite: false);
            return item;
        });
    }

    public IReadOnlyList<WorkItem> List() => WithLock(
        () => Order(ListWithoutLock()).ToArray());

    public IReadOnlyList<WorkItem> Pending() => WithLock(
        () => Order(ListWithoutLock().Where(item => item.State == WorkState.Pending)).ToArray());

    public IReadOnlyList<string> CancelRequests() => WithLock(
        () => ListWithoutLock()
            .Where(item => item.State == WorkState.Running && item.CancelRequested)
            .Select(item => item.Id)
            .ToArray());

    public WorkItem? Get(string id)
    {
        ValidateId(id);
        var path = config.WorkFile(id);
        return WithLock(() => File.Exists(path) ? Read(path) : null);
    }

    public bool TryClaim(string id) => Transition(id, WorkState.Pending, item => item with
    {
        State = WorkState.Running,
        Attempts = item.Attempts + 1,
        StartedAt = DateTimeOffset.UtcNow,
        FinishedAt = null
    });

    public bool TrySucceed(string id, int exitCode) => Transition(id, WorkState.Running, item => item with
    {
        State = WorkState.Succeeded,
        ExitCode = exitCode,
        FinishedAt = DateTimeOffset.UtcNow,
        CancelRequested = false
    });

    public bool TryFail(string id, string error, int? exitCode = null) => Transition(id, WorkState.Running, item => item with
    {
        State = WorkState.Failed,
        Error = error,
        ExitCode = exitCode,
        FinishedAt = DateTimeOffset.UtcNow,
        CancelRequested = false
    });

    public bool TryRetry(string id) => Transition(id, WorkState.Failed, item => item with
    {
        State = WorkState.Pending,
        Error = null,
        ExitCode = null,
        FinishedAt = null
    });

    // A pending item fails immediately. A running item is only flagged; its orchestrator stops it.
    public CancelResult Cancel(string id)
    {
        ValidateId(id);
        return WithLock(() =>
        {
            var path = config.WorkFile(id);
            if (!File.Exists(path)) return CancelResult.NotFound;

            var item = Read(path);
            switch (item.State)
            {
                case WorkState.Pending:
                    Write(path, item with
                    {
                        State = WorkState.Failed,
                        Error = CancelledMessage,
                        FinishedAt = DateTimeOffset.UtcNow
                    }, overwrite: true);
                    return CancelResult.Cancelled;
                case WorkState.Running:
                    Write(path, item with { CancelRequested = true }, overwrite: true);
                    return CancelResult.Requested;
                default:
                    return CancelResult.NotCancellable;
            }
        });
    }

    public WorkChange Edit(string id, string prompt)
    {
        ValidatePrompt(prompt);
        return WithPending(id, (item, path) =>
            Write(path, item with { Prompt = prompt.Trim() }, overwrite: true));
    }

    // Running work belongs to an orchestrator, so it cannot be removed. Finished work is removed
    // with its log; a worktree it used is kept.
    public WorkChange Delete(string id)
    {
        ValidateId(id);
        using var reviewLock = FileLock.OpenExclusive(config.ReviewLock(id));
        return WithLock(() =>
        {
            var path = config.WorkFile(id);
            if (!File.Exists(path)) return WorkChange.NotFound;
            if (Read(path).State == WorkState.Running) return WorkChange.Running;

            File.Delete(path);
            File.Delete(config.LogFile(id));
            return WorkChange.Done;
        });
    }

    // Called once the worktree and branch are gone. The record stays, so the prompt and log remain.
    public WorkChange MarkDiscarded(string id)
    {
        ValidateId(id);
        return WithLock(() =>
        {
            var path = config.WorkFile(id);
            if (!File.Exists(path)) return WorkChange.NotFound;

            var item = Read(path);
            if (!item.CanReview()) return WorkChange.NotReviewable;

            Write(path, item with { Discarded = true }, overwrite: true);
            return WorkChange.Done;
        });
    }

    public WorkChange MarkAccepted(string id)
    {
        ValidateId(id);
        return WithLock(() =>
        {
            var path = config.WorkFile(id);
            if (!File.Exists(path)) return WorkChange.NotFound;

            var item = Read(path);
            if (!item.CanReview() || item.Accepted) return WorkChange.NotReviewable;

            Write(path, item with { Accepted = true }, overwrite: true);
            return WorkChange.Done;
        });
    }

    // Work that was running when the process stopped: a cancelled item stays cancelled,
    // anything else goes back to pending.
    public int RecoverRunning() => WithLock(() =>
    {
        var running = ListWithoutLock()
            .Where(item => item.State == WorkState.Running)
            .ToArray();
        foreach (var item in running)
        {
            Write(config.WorkFile(item.Id), item.CancelRequested
                ? item with
                {
                    State = WorkState.Failed,
                    Error = CancelledMessage,
                    FinishedAt = DateTimeOffset.UtcNow,
                    CancelRequested = false
                }
                : item with
                {
                    State = WorkState.Pending,
                    Error = InterruptedMessage
                }, overwrite: true);
        }
        return running.Length;
    });

    private bool Transition(string id, WorkState from, Func<WorkItem, WorkItem> change)
    {
        ValidateId(id);
        return WithLock(() =>
        {
            var path = config.WorkFile(id);
            if (!File.Exists(path)) return false;
            var item = Read(path);
            if (item.State != from) return false;

            Write(path, change(item), overwrite: true);
            return true;
        });
    }

    private WorkChange WithPending(string id, Action<WorkItem, string> change)
    {
        ValidateId(id);
        return WithLock(() =>
        {
            var path = config.WorkFile(id);
            if (!File.Exists(path)) return WorkChange.NotFound;

            var item = Read(path);
            if (item.State != WorkState.Pending) return WorkChange.NotPending;

            change(item, path);
            return WorkChange.Done;
        });
    }

    private static IEnumerable<WorkItem> Order(IEnumerable<WorkItem> items) => items
        .OrderBy(item => item.CreatedAt)
        .ThenBy(item => item.Id, StringComparer.Ordinal);

    private IEnumerable<WorkItem> ListWithoutLock() => Directory.Exists(config.Work)
        ? Directory.EnumerateFiles(config.Work, "*.json").Select(TryRead).OfType<WorkItem>()
        : [];

    private WorkItem? TryRead(string path)
    {
        try
        {
            return Read(path);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        {
            // Report once per file: the run loop scans the queue on every tick.
            if (_unreadablePaths.Add(path))
                Console.Error.WriteLine($"Warning: skipping unreadable work file {path}: {error.Message}");
            return null;
        }
    }

    private static WorkItem Read(string path)
    {
        using var stream = File.OpenRead(path);
        var item = JsonSerializer.Deserialize(stream, VeloJsonContext.Default.WorkItem)
            ?? throw new InvalidDataException($"Invalid work file: {path}");
        ValidateId(item.Id);
        if (item.Id != Path.GetFileNameWithoutExtension(path)
            || string.IsNullOrWhiteSpace(item.Prompt)
            || string.IsNullOrWhiteSpace(item.Workspace)
            || !Path.IsPathFullyQualified(item.Workspace)
            || !Enum.IsDefined(item.State))
            throw new InvalidDataException($"Invalid work file: {path}");
        return item;
    }

    private static void Write(string path, WorkItem item, bool overwrite)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(
                temporaryPath,
                JsonSerializer.SerializeToUtf8Bytes(item, VeloJsonContext.Default.WorkItem));
            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static bool IsValidId(string id) =>
        !string.IsNullOrWhiteSpace(id)
        && id == Path.GetFileName(id)
        && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void ValidateId(string id)
    {
        if (!IsValidId(id)) throw new ArgumentException("Invalid work id.", nameof(id));
    }

    private static void ValidatePrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("A work prompt is required.", nameof(prompt));
    }

    private T WithLock<T>(Func<T> action)
    {
        using var _ = FileLock.OpenExclusive(config.QueueLock);
        return action();
    }
}
