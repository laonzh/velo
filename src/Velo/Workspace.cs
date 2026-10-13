using System.Diagnostics;
using System.Text;

namespace Velo;

internal sealed class Workspace(Config config)
{
    // Patches longer than this are cut off; the response says so.
    internal const int MaxPatchChars = 512 * 1024;

    public async Task<Worktree> CreateAsync(string id, string source)
    {
        var repository = Path.GetFullPath(
            (await Git.RunAsync(source, "rev-parse", "--show-toplevel")).Trim());
        // Resolve HEAD once, so the recorded base is exactly the commit the worktree starts from.
        var baseCommit = (await Git.RunAsync(repository, "rev-parse", "--verify", "HEAD^{commit}")).Trim();
        var path = Path.GetFullPath(config.WorkspacePath(id));
        var branch = $"velo/{id}";
        await Git.RunAsync(repository, "worktree", "add", "-b", branch, path, baseCommit);
        return new Worktree(repository, path, branch, baseCommit);
    }

    public async Task RemoveAsync(Worktree worktree)
    {
        try
        {
            await Git.RunAsync(worktree.Repository, "worktree", "remove", "--force", worktree.Path);
            await Git.RunAsync(worktree.Repository, "branch", "-D", worktree.Branch);
        }
        catch (InvalidOperationException error)
        {
            Console.Error.WriteLine(
                $"Warning: could not remove worktree {worktree.Path}: {error.Message}");
        }
    }

    // Shows every change against the base commit, including new files. A temporary index lets
    // `git add` see untracked files without touching the worktree's own index.
    public async Task<WorkDiff> DiffAsync(string id, string checkout, string baseCommit)
    {
        var (stat, patch) = await FullDiffAsync(id, checkout, baseCommit);
        var truncated = patch.Length > MaxPatchChars;
        return new WorkDiff(
            baseCommit,
            stat,
            truncated ? patch[..MaxPatchChars] : patch,
            truncated);
    }

    // Applies every change of a succeeded worktree to its repository as uncommitted changes. The
    // patch is checked first and nothing is written unless all of it applies. It is never capped.
    public async Task AcceptAsync(WorkItem item)
    {
        var repository = item.Repository
            ?? throw new InvalidOperationException("Work has no worktree.");
        var (_, patch) = await FullDiffAsync(item.Id, item.Workspace, item.BaseCommit!);
        if (patch.Length == 0) return;

        var file = Path.Combine(Path.GetTempPath(), $"velo-{item.Id}-{Guid.NewGuid():N}.patch");
        await File.WriteAllTextAsync(file, patch, new UTF8Encoding(false));
        try
        {
            try
            {
                await Git.RunAsync(repository, "apply", "--check", file);
            }
            catch (InvalidOperationException error)
            {
                throw new PatchConflictException(error.Message);
            }
            await Git.RunAsync(repository, "apply", file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // Shows every change against the base commit, including new files. A temporary index lets
    // `git add` see untracked files without touching the worktree's own index.
    private static async Task<(string Stat, string Patch)> FullDiffAsync(
        string id, string checkout, string baseCommit)
    {
        var index = Path.Combine(Path.GetTempPath(), $"velo-{id}-{Guid.NewGuid():N}.index");
        var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        try
        {
            await Git.RunAsync(checkout, environment, "add", "--all");
            var stat = await Git.RunAsync(
                checkout, environment, "diff", "--cached", "--no-color", "--no-ext-diff", "--stat", baseCommit);
            var patch = await Git.RunAsync(
                checkout, environment, "diff", "--cached", "--no-color", "--no-ext-diff", baseCommit);
            return (stat, patch);
        }
        finally
        {
            File.Delete(index);
        }
    }

    // Removes the worktree and its branch, including any uncommitted or committed changes on it.
    // A worktree directory that is already gone is pruned, so a discard can still finish.
    public async Task DiscardAsync(WorkItem item)
    {
        var repository = item.Repository
            ?? throw new InvalidOperationException("Work has no worktree.");
        var branch = $"velo/{item.Id}";
        if (Directory.Exists(item.Workspace))
            await Git.RunAsync(repository, "worktree", "remove", "--force", item.Workspace);
        await Git.RunAsync(repository, "worktree", "prune");
        if ((await Git.RunAsync(repository, "branch", "--list", branch)).Trim().Length > 0)
            await Git.RunAsync(repository, "branch", "-D", branch);
    }

    public static string Key(string path)
    {
        var normalized = Normalize(path);
        for (var directory = new DirectoryInfo(normalized);
             directory is not null;
             directory = directory.Parent)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return Normalize(directory.FullName);
        }
        return normalized;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal sealed record Worktree(string Repository, string Path, string Branch, string BaseCommit);

    internal sealed record WorkDiff(string Base, string Stat, string Patch, bool Truncated);
}

// The changes do not apply to the repository as they are now; nothing was written.
internal sealed class PatchConflictException(string message) : InvalidOperationException(message);

internal static class Git
{
    public static Task<string> RunAsync(string workingDirectory, params string[] arguments) =>
        RunAsync(workingDirectory, new Dictionary<string, string>(), arguments);

    public static async Task<string> RunAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var (name, value) in environment) startInfo.Environment[name] = value;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(error.Length == 0
                ? $"git exited with code {process.ExitCode}."
                : error);
        }
        return output;
    }
}
