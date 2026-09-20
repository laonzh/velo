namespace Velo.Configuration;

public static class VeloPaths
{
    public static string RootDir { get; } =
        Environment.GetEnvironmentVariable("VELO_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velo");

    public static string PidFile => Path.Combine(RootDir, "velo.pid");
    public static string StopFlag => Path.Combine(RootDir, "stop.flag");

    public static string ProjectsDir => Path.Combine(RootDir, "projects");

    public static string GetProjectDir(string projectId) => Path.Combine(ProjectsDir, projectId);
    public static string GetWorksDir(string projectId, string state) => Path.Combine(ProjectsDir, projectId, "works",
    state);
    public static string GetLogsDir(string projectId) => Path.Combine(ProjectsDir, projectId, "logs");
    public static string GetWorkspacesDir(string projectId) => Path.Combine(ProjectsDir, projectId, "workspaces");

    public static void EnsureInitialized()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(ProjectsDir);
    }
}