namespace Velo;

/// <summary>The data directory (VELO_HOME) and every path below it.</summary>
public sealed class VeloPaths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string StateFile => Path.Combine(Root, "state.json");
    public string Logs(int number) => Path.Combine(Root, "logs", "vl-" + number);
    public string RunLog(int number, int run) => Path.Combine(Logs(number), $"run-{run}.log");
    public string Workspace(int number) => Path.Combine(Root, "workspaces", "vl-" + number);
    public string Tmp(int number) => Path.Combine(Root, "tmp", "vl-" + number);

    /// <summary>
    /// VELO_HOME, else ~/.velo on every platform.
    /// </summary>
    public static VeloPaths Resolve()
    {
        if (Environment.GetEnvironmentVariable("VELO_HOME") is { Length: > 0 } home) return new VeloPaths(home);
        return new VeloPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velo"));
    }
}
