using System.Runtime.InteropServices;

namespace Velo;

/// <summary>
/// Finds the codex executable: the configured path, else <c>codex</c> on PATH; Windows npm shims → native codex.exe.
/// </summary>
internal static class CodexLocator
{
    internal static (string? Exe, bool Npm, string? Error) Locate(string? configured)
    {
        string? requested;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
            requested = File.Exists(full) ? full : File.Exists(full + ".exe") ? full + ".exe" : null;
            if (requested is null) return (null, false, $"Configured codex path '{full}' does not exist.");
        }
        else
        {
            requested = FindOnPath("codex", Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"));
            if (requested is null) return (null, false, "Codex CLI was not found on PATH. Install it (npm install -g @openai/codex) or set 'codexPath'.");
        }
        (string? exe, bool npm) = ResolveExecutable(requested);
        return exe is null
            ? (null, false, $"'{requested}' is a script shim and the native codex.exe could not be resolved from it. Set 'codexPath' to codex.exe.")
            : (exe, npm, null);
    }

    /// <summary>
    /// PATH lookup. On Windows only PATHEXT candidates count (an extension-less "codex" is the npm sh script).
    /// </summary>
    internal static string? FindOnPath(string name, string? pathVariable, string? pathExt)
    {
        if (string.IsNullOrEmpty(pathVariable)) return null;
        string[] exts = OperatingSystem.IsWindows()
            ? (string.IsNullOrEmpty(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt).Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (string dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (string ext in exts)
            {
                string candidate = Path.Combine(dir.Trim().Trim('"'), name + ext.ToLowerInvariant());
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        return null;
    }

    /// <summary>
    /// Maps a Windows npm shim (.cmd/.bat/.ps1) to the native codex.exe in the package next to it (null if not found).
    /// </summary>
    internal static (string? Executable, bool ManagedByNpm) ResolveExecutable(string path)
    {
        if (!OperatingSystem.IsWindows() || Path.GetExtension(path).ToLowerInvariant() is not (".cmd" or ".bat" or ".ps1" or ""))
            return (path, false);

        string packageRoot = Path.Combine(Path.GetDirectoryName(path)!, "node_modules", "@openai", "codex");
        (string triple, string platformPackage) = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? ("aarch64-pc-windows-msvc", "codex-win32-arm64")
            : ("x86_64-pc-windows-msvc", "codex-win32-x64");
        string nodeModules = Path.GetDirectoryName(Path.GetDirectoryName(packageRoot)!)!;
        string[] candidates =
        [
            Path.Combine(packageRoot, "node_modules", "@openai", platformPackage, "vendor", triple, "bin", "codex.exe"), // nested optional dependency
            Path.Combine(nodeModules, "@openai", platformPackage, "vendor", triple, "bin", "codex.exe"), // hoisted
            Path.Combine(packageRoot, "vendor", triple, "bin", "codex.exe"), // legacy layout
        ];
        return candidates.FirstOrDefault(File.Exists) is { } hit ? (Path.GetFullPath(hit), true) : (null, false);
    }
}
