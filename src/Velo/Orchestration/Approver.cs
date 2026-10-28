namespace Velo;

/// <summary>The merge of approve under one process-wide merge lock, and the file / git cleanup of rm.</summary>
internal sealed class Approver(VeloPaths paths, Store store)
{
    private readonly SemaphoreSlim _mergeLock = new(1, 1);

    /// <summary>review → done; a git issue is first merged (--no-ff) into the repository's current branch.</summary>
    public async Task<Issue> ApproveAsync(Issue issue, Func<Issue> reload)
    {
        CheckReview(issue);
        if (issue.Directory is { } repo)
        {
            await _mergeLock.WaitAsync();
            try
            {
                CheckReview(issue = reload());
                string worktree = paths.Workspace(issue.Number), branch = Git.Branch(store.HomeId, issue.Number);
                if (Directory.Exists(worktree))
                {
                    await Git.ValidateWorktreeAsync(repo, worktree, branch);
                    await Git.CommitAllAsync(worktree, $"{issue.Key}: manual changes");
                }
                if (await Git.BranchExistsAsync(repo, branch) && !await Git.IsMergedAsync(repo, branch))
                {
                    // the merge goes into whatever is checked out: refuse anything but the branch the issue started from
                    string? baseBranch = await Git.BaseBranchAsync(repo, branch), current = await Git.CurrentBranchAsync(repo);
                    if (baseBranch is not null && current != baseBranch)
                        throw VeloException.Conflict($"{issue.Key} was branched from {baseBranch}, but {repo} has {current} checked out. "
                            + $"Check out {baseBranch} and approve again.");
                    try
                    {
                        await Git.MergeAsync(repo, branch, $"Merge {issue.Key}: {issue.Title}");
                    }
                    catch (MergeConflictException ex)
                    {
                        string target = await Git.CurrentBranchAsync(repo);
                        throw VeloException.Conflict($"{issue.Key} conflicts with {target} in {string.Join(", ", ex.Files)}. "
                            + $"In {worktree} run `git merge {target}`, resolve the conflicts and commit, then approve again.");
                    }
                }
                await Git.RemoveWorktreeAsync(repo, worktree, branch, forceBranch: false);
            }
            finally
            {
                _mergeLock.Release();
            }
        }
        return store.Update(issue.Number, i =>
        {
            CheckReview(i);
            return i with { Status = IssueStatus.Done, UpdatedAt = DateTimeOffset.UtcNow };
        });
    }

    /// <summary>
    /// Deletes the worktree and branch (-D), workspace, tmp and logs; failures leave the issue available for retry.
    /// </summary>
    public async Task CleanUpAsync(Issue issue)
    {
        string workspace = paths.Workspace(issue.Number);
        if (issue.Directory is not null)
        {
            await Git.RemoveWorktreeAsync(issue.Directory, workspace, Git.Branch(store.HomeId, issue.Number), forceBranch: true);
        }
        else DeleteDirectory(workspace);
        foreach (string dir in new[] { paths.Tmp(issue.Number), paths.Logs(issue.Number) })
        {
            DeleteDirectory(dir);
        }
    }

    private static void CheckReview(Issue issue)
    {
        if (issue.Status != IssueStatus.Review)
            throw VeloException.Conflict($"{issue.Key} is {Engine.Name(issue.Status)}; only review issues can be approved");
    }

    /// <summary>Recursive delete that also removes read-only files (git objects, tool caches).</summary>
    private static void DeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }
}
