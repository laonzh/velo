using System.Diagnostics;
using System.Text;

namespace Velo;

public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// The git commands Velo needs. Branches are scoped to the persisted HOME identity.
/// </summary>
public static class Git
{
    public const int MaxDiffChars = 1024 * 1024;

    // `worktree add/remove/prune` touch the repository's shared worktree list: one at a time in this process.
    private static readonly SemaphoreSlim s_worktreeLock = new(1, 1);

    public static string Branch(string homeId, int number) => $"velo/{homeId}/vl-{number}";

    /// <summary>Runs <c>git -C dir args...</c> with stdin closed and no prompts.</summary>
    public static async Task<GitResult> RunAsync(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in (string[])["-C", dir, "-c", "core.quotepath=off", .. args]) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using Process process = Process.Start(psi) ?? throw new GitException("cannot start git");
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new GitResult(process.ExitCode, await output, await error);
    }

    /// <summary>Like <see cref="RunAsync"/>, but a non-zero exit code throws a <see cref="GitException"/>.</summary>
    public static async Task<string> CheckAsync(string dir, params string[] args)
    {
        GitResult r = await RunAsync(dir, args);
        if (r.Ok) return r.Output;
        string command = string.Join(' ', args.SkipWhile(a => a == "-c" || a.Contains('=')).Take(2));
        throw new GitException($"git {command} failed: {(r.Error + r.Output).Trim()}");
    }

    /// <summary>
    /// The repository root containing <paramref name="dir"/>, or null when it is not inside a git work tree.
    /// </summary>
    public static async Task<string?> TopLevelAsync(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        GitResult r = await RunAsync(dir, "rev-parse", "--show-toplevel");
        return r.Ok && r.Output.Trim().Length > 0 ? Path.GetFullPath(r.Output.Trim()) : null;
    }

    /// <summary>
    /// False for a freshly initialised repository: HEAD points at a branch with no commit,
    /// so no worktree can be created.
    /// </summary>
    public static async Task<bool> HasCommitsAsync(string repo) => (await RunAsync(repo, "rev-parse", "-q", "--verify", "HEAD")).Ok;

    public static async Task<bool> BranchExistsAsync(string repo, string branch) =>
        (await RunAsync(repo, "show-ref", "--verify", "--quiet", "refs/heads/" + branch)).Ok;

    public static async Task<string> CurrentBranchAsync(string repo) => (await CheckAsync(repo, "rev-parse", "--abbrev-ref", "HEAD")).Trim();

    /// <summary>
    /// The branch <paramref name="branch"/> was created from, or null when unknown
    /// (detached HEAD, or created by an older Velo).
    /// </summary>
    public static async Task<string?> BaseBranchAsync(string repo, string branch)
    {
        GitResult r = await RunAsync(repo, "config", "--get", BaseKey(branch));
        return r.Ok && r.Output.Trim() is { Length: > 0 } b ? b : null;
    }

    // Kept in the branch's own config section, which git deletes together with the branch.
    private static string BaseKey(string branch) => $"branch.{branch}.veloBase";

    /// <summary>
    /// Creates the worktree <paramref name="path"/> on <paramref name="branch"/> (new from HEAD,
    /// or the existing branch) unless it exists.
    /// </summary>
    public static async Task EnsureWorktreeAsync(string repo, string path, string branch)
    {
        await s_worktreeLock.WaitAsync();
        try
        {
            if (Directory.Exists(path))
            {
                await ValidateWorktreeAsync(repo, path, branch);
                return;
            }
            await CheckAsync(repo, "worktree", "prune"); // forget a worktree whose directory was deleted by hand
            if (await BranchExistsAsync(repo, branch))
            {
                await CheckAsync(repo, "worktree", "add", path, branch);
                return;
            }
            string head = await CurrentBranchAsync(repo);
            await CheckAsync(repo, "worktree", "add", "-b", branch, path, "HEAD");
            if (head != "HEAD") await CheckAsync(repo, "config", BaseKey(branch), head); // detached HEAD: no base to remember
        }
        finally
        {
            s_worktreeLock.Release();
        }
    }

    public static async Task ValidateWorktreeAsync(string repo, string path, string branch)
    {
        static string ResolvePath(string path)
        {
            string full = Path.GetFullPath(path);
            string resolved = Path.GetPathRoot(full)!;
            foreach (string segment in full[resolved.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = new DirectoryInfo(Path.Combine(resolved, segment));
                resolved = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
            }
            return Path.TrimEndingDirectorySeparator(resolved);
        }

        static bool SamePath(string left, string right) => string.Equals(
            ResolvePath(left),
            ResolvePath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        string? topLevel = await TopLevelAsync(path);
        if (topLevel is null || !SamePath(topLevel, path))
            throw new GitException($"{path} is not the expected task worktree; no changes were committed");
        string expected = (await CheckAsync(repo, "rev-parse", "--path-format=absolute", "--git-common-dir")).Trim();
        string actual = (await CheckAsync(path, "rev-parse", "--path-format=absolute", "--git-common-dir")).Trim();
        if (!SamePath(expected, actual))
            throw new GitException($"{path} belongs to another repository; no changes were committed");
        string current = await CurrentBranchAsync(path);
        if (current != branch)
            throw new GitException($"{path} has {current} checked out, expected {branch}; restore the task branch before retrying");
    }

    /// <summary>
    /// Removes the worktree and deletes the branch (<c>-d</c>, or <c>-D</c> when <paramref name="forceBranch"/>).
    /// </summary>
    public static async Task RemoveWorktreeAsync(string repo, string path, string branch, bool forceBranch)
    {
        await s_worktreeLock.WaitAsync();
        try
        {
            if (Directory.Exists(path))
            {
                await ValidateWorktreeAsync(repo, path, branch);
                await CheckAsync(repo, "worktree", "remove", "--force", path);
            }
            await CheckAsync(repo, "worktree", "prune");
        }
        finally
        {
            s_worktreeLock.Release();
        }
        if (await BranchExistsAsync(repo, branch)) await CheckAsync(repo, "branch", forceBranch ? "-D" : "-d", branch);
    }

    /// <summary><c>git add -A</c> + <c>git commit</c>; returns false when there was nothing to commit.</summary>
    public static async Task<bool> CommitAllAsync(string dir, string message)
    {
        string unmerged = await CheckAsync(dir, "ls-files", "--unmerged");
        if (unmerged.Length > 0 || await HasMergeAsync(dir))
            throw new GitException("an unresolved conflict or merge is in progress; finish or abort it before Velo commits changes");
        await CheckAsync(dir, "add", "-A");
        GitResult staged = await RunAsync(dir, "diff", "--cached", "--quiet");
        if (staged.Ok) return false;
        if (staged.ExitCode != 1) throw new GitException("git diff --cached failed: " + staged.Error.Trim());
        await CheckAsync(dir, [.. await CommitOptionsAsync(dir), "commit", "-q", "-m", message]);
        return true;
    }

    /// <summary>
    /// The committed changes of <paramref name="branch"/> since its merge base with HEAD
    /// (empty when the branch does not exist).
    /// </summary>
    public static async Task<string> DiffAsync(string repo, string branch)
    {
        if (!await BranchExistsAsync(repo, branch)) return "";
        string diff = await CheckAsync(repo, "diff", "--no-color", "--no-ext-diff", "HEAD..." + branch);
        return diff.Length <= MaxDiffChars ? diff : diff[..MaxDiffChars] + "\n[diff truncated at 1 MiB]\n";
    }

    /// <summary>True when <paramref name="branch"/> is already contained in HEAD.</summary>
    public static async Task<bool> IsMergedAsync(string repo, string branch)
    {
        GitResult r = await RunAsync(repo, "merge-base", "--is-ancestor", branch, "HEAD");
        return r.Ok || (r.ExitCode == 1 ? false : throw new GitException("git merge-base failed: " + r.Error.Trim()));
    }

    /// <summary>
    /// <c>git merge --no-ff</c>; refuses an existing merge and aborts only a merge started by this call on failure.
    /// </summary>
    public static async Task MergeAsync(string repo, string branch, string message)
    {
        if (await HasMergeAsync(repo)) throw new GitException("a merge is already in progress; finish or abort it before approving");
        GitResult r = await RunAsync(repo, [.. await CommitOptionsAsync(repo), "merge", "--no-ff", branch, "-m", message]);
        if (r.Ok) return;
        string[] conflicts = [.. (await RunAsync(repo, "diff", "--name-only", "--diff-filter=U")).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        if (await HasMergeAsync(repo)) await RunAsync(repo, "merge", "--abort");
        string text = (r.Output + "\n" + r.Error).Trim();
        throw conflicts.Length > 0 ? new MergeConflictException(conflicts, text) : new GitException(text);
    }

    /// <summary>
    /// How approving would go: commits ahead / behind the current branch and the paths that would conflict
    /// (nothing is changed).
    /// </summary>
    public static async Task<MergeCheck> MergeCheckAsync(string repo, string branch)
    {
        string target = await CurrentBranchAsync(repo);
        if (!await BranchExistsAsync(repo, branch)) return new MergeCheck(target, 0, 0, []);
        string[] counts = (await CheckAsync(repo, "rev-list", "--left-right", "--count", "HEAD..." + branch)).Split((char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        // merge-tree computes the merge in memory: exit 0 clean, 1 conflicts (paths after the tree id), anything else = unsupported
        GitResult m = await RunAsync(repo, "merge-tree", "--write-tree", "--name-only", "--no-messages", "HEAD", branch);
        string[]? conflicts = m.ExitCode switch
        {
            0 => [],
            1 => [.. m.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).Distinct()],
            _ => null,
        };
        return new MergeCheck(target, int.Parse(counts[1]), int.Parse(counts[0]), conflicts);
    }

    private static async Task<bool> HasMergeAsync(string repo) =>
        (await RunAsync(repo, "rev-parse", "-q", "--verify", "MERGE_HEAD")).Ok;

    // Unattended commits: never prompt for a signing passphrase; fall back to a Velo identity when none is configured.
    private static async Task<string[]> CommitOptionsAsync(string dir) =>
        (await RunAsync(dir, "config", "user.name")).Ok && (await RunAsync(dir, "config", "user.email")).Ok
            ? ["-c", "commit.gpgsign=false"]
            : ["-c", "commit.gpgsign=false", "-c", "user.name=Velo", "-c", "user.email=velo@localhost"];
}
