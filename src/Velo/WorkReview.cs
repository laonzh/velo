using System.ComponentModel;

namespace Velo;

internal enum ReviewResult { Done, NotFound, NotReviewable, WorktreeMissing, AlreadyAccepted, Conflict, Failed }

internal sealed record ReviewOutcome(ReviewResult Result, string Message, Workspace.WorkDiff? Diff = null);

// Reviews finished worktree work for the CLI and the HTTP API alike.
internal sealed class WorkReview(Config config, WorkQueue queue, Workspace workspace)
{
    private const string NotReviewableMessage =
        "Only succeeded work with a worktree that has not been discarded can be reviewed or discarded.";

    private const string AlreadyAcceptedMessage = "This work was already accepted.";

    public async Task<ReviewOutcome> DiffAsync(string id)
    {
        using var reviewLock = await LockAsync(id);
        var item = queue.Get(id);
        if (item is null) return NotFound(id);
        if (!item.CanReview()) return new ReviewOutcome(ReviewResult.NotReviewable, NotReviewableMessage);
        if (!Directory.Exists(item.Workspace))
            return new ReviewOutcome(ReviewResult.WorktreeMissing, $"The worktree is missing: {item.Workspace}");

        try
        {
            var diff = await workspace.DiffAsync(id, item.Workspace, item.BaseCommit!);
            return new ReviewOutcome(ReviewResult.Done, string.Empty, diff);
        }
        catch (Exception error) when (IsGitFailure(error))
        {
            return new ReviewOutcome(ReviewResult.Failed, error.Message);
        }
    }

    public async Task<ReviewOutcome> DiscardAsync(string id)
    {
        using var reviewLock = await LockAsync(id);
        var item = queue.Get(id);
        if (item is null) return NotFound(id);
        if (!item.CanReview()) return new ReviewOutcome(ReviewResult.NotReviewable, NotReviewableMessage);

        try
        {
            await workspace.DiscardAsync(item);
        }
        catch (Exception error) when (IsGitFailure(error))
        {
            return new ReviewOutcome(ReviewResult.Failed, error.Message);
        }

        // The worktree is gone at this point. A record deleted meanwhile is reported as not found.
        return queue.MarkDiscarded(id) switch
        {
            WorkChange.Done => new ReviewOutcome(ReviewResult.Done, string.Empty),
            WorkChange.NotFound => NotFound(id),
            _ => new ReviewOutcome(ReviewResult.NotReviewable, NotReviewableMessage)
        };
    }

    public async Task<ReviewOutcome> AcceptAsync(string id)
    {
        using var reviewLock = await LockAsync(id);
        var item = queue.Get(id);
        if (item is null) return NotFound(id);
        if (item.Accepted) return new ReviewOutcome(ReviewResult.AlreadyAccepted, AlreadyAcceptedMessage);
        if (!item.CanReview()) return new ReviewOutcome(ReviewResult.NotReviewable, NotReviewableMessage);
        if (!Directory.Exists(item.Workspace))
            return new ReviewOutcome(ReviewResult.WorktreeMissing, $"The worktree is missing: {item.Workspace}");

        try
        {
            await workspace.AcceptAsync(item);
        }
        catch (PatchConflictException error)
        {
            return new ReviewOutcome(
                ReviewResult.Conflict,
                $"The changes do not apply cleanly to {item.Repository}, so nothing was changed: {error.Message}");
        }
        catch (Exception error) when (IsGitFailure(error))
        {
            return new ReviewOutcome(ReviewResult.Failed, error.Message);
        }

        // The changes are in the repository at this point. A record deleted meanwhile is reported as not found.
        return queue.MarkAccepted(id) switch
        {
            WorkChange.Done => new ReviewOutcome(ReviewResult.Done, string.Empty),
            WorkChange.NotFound => NotFound(id),
            _ => new ReviewOutcome(ReviewResult.NotReviewable, NotReviewableMessage)
        };
    }

    private static ReviewOutcome NotFound(string id) =>
        new(ReviewResult.NotFound, $"Work not found: {id}");

    private Task<FileStream> LockAsync(string id)
    {
        if (!WorkQueue.IsValidId(id)) throw new ArgumentException("Invalid work id.", nameof(id));
        return FileLock.OpenExclusiveAsync(config.ReviewLock(id));
    }

    // git failures arrive as InvalidOperationException; a missing repository directory fails to start git.
    private static bool IsGitFailure(Exception error) =>
        error is InvalidOperationException or IOException or Win32Exception;
}
