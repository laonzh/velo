using System.Text.Json;

namespace Velo;

public sealed class TaskStore
{
    private static readonly TaskState[] States = Enum.GetValues<TaskState>();

    public void Initialize() => VeloPaths.Initialize();

    internal static string NewId()
    {
        var now = DateTimeOffset.UtcNow;
        return $"{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..23];
    }

    public TaskEntry Add(string title, string workspacePath)
    {
        while (true)
        {
            var id = NewId();
            try
            {
                return Add(id, title, workspacePath);
            }
            catch (IOException) when (FindState(id) is not null)
            {
            }
        }
    }

    internal TaskEntry Add(string id, string title, string workspacePath)
    {
        ValidateId(id);
        var now = DateTimeOffset.UtcNow;
        var item = new TaskItem(title, Path.GetFullPath(workspacePath), now, now);
        Write(VeloPaths.TaskFile(TaskState.Todo, id), item, overwrite: false);
        return new TaskEntry(id, TaskState.Todo, item);
    }

    public IReadOnlyList<TaskEntry> List() => States
        .SelectMany(List)
        .OrderByDescending(entry => entry.Item.CreatedAtUtc)
        .ToArray();

    public TaskEntry? Get(string id)
    {
        ValidateId(id);
        var state = FindState(id);
        return state is null ? null : ReadEntry(state.Value, id);
    }

    public IReadOnlyList<TaskEntry> Pending(int limit) => limit <= 0
        ? []
        : List(TaskState.Todo)
            .OrderBy(entry => entry.Item.CreatedAtUtc)
            .Take(limit)
            .ToArray();

    public bool Claim(string id) => Move(id, [TaskState.Todo], TaskState.Running);

    public bool Complete(string id) => Move(id, [TaskState.Running], TaskState.Done);

    public bool Fail(string id, string reason) =>
        Move(id, [TaskState.Running], TaskState.Failed, reason);

    public bool Cancel(string id) =>
        Move(id, [TaskState.Todo, TaskState.Running], TaskState.Cancelled);

    public bool Retry(string id) =>
        Move(id, [TaskState.Failed, TaskState.Cancelled], TaskState.Todo, clearError: true);

    public int RecoverRunning()
    {
        var count = 0;
        foreach (var entry in List(TaskState.Running))
        {
            if (Move(entry.Id, [TaskState.Running], TaskState.Todo)) count++;
        }
        return count;
    }

    private static IEnumerable<TaskEntry> List(TaskState state)
    {
        foreach (var path in Directory.EnumerateFiles(VeloPaths.StateDirectory(state), "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            yield return new TaskEntry(id, state, Read(path));
        }
    }

    private static TaskEntry ReadEntry(TaskState state, string id) =>
        new(id, state, Read(VeloPaths.TaskFile(state, id)));

    private static TaskItem Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, VeloJsonContext.Default.TaskItem)
            ?? throw new InvalidDataException($"Invalid task file: {path}");
    }

    private static void Write(string path, TaskItem item, bool overwrite)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(stream, item, VeloJsonContext.Default.TaskItem);
            }
            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    private static bool Move(
        string id,
        IReadOnlyList<TaskState> sourceStates,
        TaskState targetState,
        string? error = null,
        bool clearError = false)
    {
        ValidateId(id);
        foreach (var sourceState in sourceStates)
        {
            var source = VeloPaths.TaskFile(sourceState, id);
            if (!File.Exists(source)) continue;

            var target = VeloPaths.TaskFile(targetState, id);
            try
            {
                File.Move(source, target, overwrite: false);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                return false;
            }

            var current = Read(target);
            var item = current with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LastError = clearError ? null : error ?? current.LastError
            };
            Write(target, item, overwrite: true);
            return true;
        }
        return false;
    }

    private static TaskState? FindState(string id)
    {
        ValidateId(id);
        foreach (var state in States)
        {
            if (File.Exists(VeloPaths.TaskFile(state, id))) return state;
        }
        return null;
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id != Path.GetFileName(id)
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Invalid task id.", nameof(id));
        }
    }
}

