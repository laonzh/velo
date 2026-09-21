namespace Velo.Configuration;

public sealed record VeloConfig
{
    public string DefaultAgent { get; init;} = "codex";
    public int MaxConcurrency { get; init;} = 3;
    public TimeSpan PollingInterval { get; init;} = TimeSpan.FromSeconds(3);
    public TimeSpan TaskTimeout { get; init;} = TimeSpan.FromMinutes(30);

    public static string RootDir { get; } =
        Environment.GetEnvironmentVariable("VELO_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velo");
    public static string LogsDir => Path.Combine(RootDir, "logs");
    public static string WorkspacesDir => Path.Combine(RootDir, "workspaces");
    public static string TasksDir => Path.Combine(RootDir, "tasks");
    public static string PidFile => Path.Combine(RootDir, "velo.pid");
    public static string StopFlag => Path.Combine(RootDir, "stop.flag");

    public static void EnsureInitialized()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(WorkspacesDir);
        Directory.CreateDirectory(TasksDir);
    }
}