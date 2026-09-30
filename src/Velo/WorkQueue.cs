using System.Text.Json;

namespace Velo;

internal sealed class WorkQueue(Config config)
{
    internal static string NewId() =>
        $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..23];

    public WorkItem Add(string prompt, string workspace) => Add(NewId(), prompt, workspace);

    internal WorkItem Add(string id, string prompt, string workspace)
    {
        ValidateId(id);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("A work prompt is required.", nameof(prompt));
        if (!Directory.Exists(workspace))
            throw new DirectoryNotFoundException($"Workspace does not exist: {workspace}");

        var item = new WorkItem(
            id,
            WorkState.Pending,
            prompt.Trim(),
            Path.GetFullPath(workspace),
            DateTimeOffset.UtcNow);
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

    public WorkItem? Get(string id)
    {
        ValidateId(id);
        var path = config.WorkFile(id);
        return WithLock(() => File.Exists(path) ? Read(path) : null);
    }

    public bool TryClaim(string id) => Transition(id, WorkState.Pending, WorkState.Running);
    public bool TrySucceed(string id) => Transition(id, WorkState.Running, WorkState.Succeeded);
    public bool TryRetry(string id) => Transition(id, WorkState.Failed, WorkState.Pending, clearError: true);
    public bool TryFail(string id, string error) =>
        Transition(id, WorkState.Running, WorkState.Failed, error);

    public int RecoverRunning() => WithLock(() =>
    {
        var running = ListWithoutLock()
            .Where(item => item.State == WorkState.Running)
            .ToArray();
        foreach (var item in running)
        {
            Write(config.WorkFile(item.Id), item with
            {
                State = WorkState.Pending,
                Error = "Interrupted before completion."
            }, overwrite: true);
        }
        return running.Length;
    });

    private bool Transition(
        string id,
        WorkState from,
        WorkState to,
        string? error = null,
        bool clearError = false)
    {
        ValidateId(id);
        return WithLock(() =>
        {
            var path = config.WorkFile(id);
            if (!File.Exists(path)) return false;
            var item = Read(path);
            if (item.State != from) return false;

            Write(path, item with
            {
                State = to,
                Error = clearError ? null : error ?? item.Error
            }, overwrite: true);
            return true;
        });
    }

    private static IEnumerable<WorkItem> Order(IEnumerable<WorkItem> items) => items
        .OrderBy(item => item.CreatedAt)
        .ThenBy(item => item.Id, StringComparer.Ordinal);

    private IEnumerable<WorkItem> ListWithoutLock() => Directory.Exists(config.Work)
        ? Directory.EnumerateFiles(config.Work, "*.json").Select(Read)
        : [];

    private static WorkItem Read(string path)
    {
        using var stream = File.OpenRead(path);
        var item = JsonSerializer.Deserialize(stream, VeloJsonContext.Default.WorkItem)
            ?? throw new InvalidDataException($"Invalid work file: {path}");
        ValidateId(item.Id);
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

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id != Path.GetFileName(id)
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Invalid work id.", nameof(id));
        }
    }

    private T WithLock<T>(Func<T> action)
    {
        using var _ = FileLock.OpenExclusive(config.QueueLock);
        return action();
    }
}
