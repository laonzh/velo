namespace Velo;

public static class VeloPaths
{
    private static readonly object LogLock = new();

    public static string Root { get; } =
        Environment.GetEnvironmentVariable("VELO_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velo");

    public static string Tasks => Path.Combine(Root, "tasks");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Workspaces => Path.Combine(Root, "workspaces");
    public static string VeloLog => Path.Combine(Root, "velo.log");
    public static string PidFile => Path.Combine(Root, "velo.pid");
    public static string StopFile => Path.Combine(Root, "stop.flag");

    public static string StateDirectory(TaskState state) =>
        Path.Combine(Tasks, StateName(state));

    public static string TaskFile(TaskState state, string id) =>
        Path.Combine(StateDirectory(state), id + ".json");

    public static string LogFile(string id) => Path.Combine(Logs, id + ".log");

    public static void WriteLog(string level, string message)
    {
        lock (LogLock)
        {
            using var stream = new FileStream(
                VeloLog,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.WriteLine($"[{DateTimeOffset.UtcNow:O}] [{level}] {message}");
        }
    }

    public static void Initialize()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Tasks);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Workspaces);
        foreach (var state in Enum.GetValues<TaskState>())
        {
            Directory.CreateDirectory(StateDirectory(state));
        }
    }

    public static string StateName(TaskState state) => state switch
    {
        TaskState.Todo => "todo",
        TaskState.Running => "running",
        TaskState.Done => "done",
        TaskState.Failed => "failed",
        TaskState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}
