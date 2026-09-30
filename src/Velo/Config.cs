namespace Velo;

internal sealed class Config
{
    internal Config(string home)
    {
        Home = Normalize(home);
    }

    public string Home { get; }
    public string Work => Path.Combine(Home, "work");
    public string Logs => Path.Combine(Home, "logs");
    public string Workspaces => Path.Combine(Home, "workspaces");
    public string RunLock => Path.Combine(Home, "run.lock");
    public string QueueLock => Path.Combine(Home, "queue.lock");

    public string WorkFile(string id) => Path.Combine(Work, id + ".json");
    public string LogFile(string id) => Path.Combine(Logs, id + ".log");
    public string WorkspacePath(string id) => Path.Combine(Workspaces, id);

    public static Config Load() => new(
        Environment.GetEnvironmentVariable("VELO_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velo"));

    public int Concurrency()
    {
        var value = Environment.GetEnvironmentVariable("VELO_CONCURRENCY");
        if (value is null) return 2;
        if (int.TryParse(value, out var concurrency) && concurrency > 0) return concurrency;
        throw new InvalidOperationException("VELO_CONCURRENCY must be a positive integer.");
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Workspaces);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(
            new DirectoryInfo(Path.GetFullPath(path)).FullName);
}
