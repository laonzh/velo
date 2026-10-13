namespace Velo.Tests;

[Collection("Sequential")]
public sealed class WorkReviewTests
{
    [Fact]
    public async Task ReviewOperationsAndDeleteWaitForTheSameWorkLock()
    {
        await using var harness = new CliFixture();
        var repository = harness.Workspace("repository");
        await TestGit.InitRepositoryAsync(repository);

        var config = new Config(harness.VeloHome);
        config.Initialize();
        var queue = new WorkQueue(config);
        var workspace = new Workspace(config);
        var id = await new WorkIntake(queue, workspace).AddAsync("review", repository, worktree: true);
        Assert.True(queue.TryClaim(id));
        Assert.True(queue.TrySucceed(id, 0));
        var worktree = queue.Get(id)!.Workspace;
        await File.WriteAllTextAsync(Path.Combine(worktree, "change.txt"), "accepted change");

        var review = new WorkReview(config, queue, workspace);
        Task<ReviewOutcome> accepting;
        Task<ReviewOutcome> discarding;
        using (var held = FileLock.OpenExclusive(config.ReviewLock(id)))
        {
            accepting = review.AcceptAsync(id);
            discarding = review.DiscardAsync(id);
            await Task.Delay(300);
            Assert.False(accepting.IsCompleted);
            Assert.False(discarding.IsCompleted);
            Assert.True(Directory.Exists(worktree));
            Assert.False(File.Exists(Path.Combine(repository, "change.txt")));
        }
        var accepted = await accepting;
        var discarded = await discarding;
        Assert.Equal(ReviewResult.Done, discarded.Result);
        Assert.Contains(accepted.Result, new[] { ReviewResult.Done, ReviewResult.NotReviewable });
        Assert.Equal(accepted.Result == ReviewResult.Done, queue.Get(id)!.Accepted);
        Assert.Equal(accepted.Result == ReviewResult.Done, File.Exists(Path.Combine(repository, "change.txt")));
        Assert.True(queue.Get(id)!.Discarded);

        Task<WorkChange> deleting;
        using (var held = FileLock.OpenExclusive(config.ReviewLock(id)))
        {
            deleting = Task.Run(() => queue.Delete(id));
            await Task.Delay(100);
            Assert.False(deleting.IsCompleted);
        }
        Assert.Equal(WorkChange.Done, await deleting);
    }
}
