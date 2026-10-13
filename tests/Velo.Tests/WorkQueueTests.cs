using System.Text.Json.Nodes;

namespace Velo.Tests;

public sealed class WorkQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Velo.Tests", Guid.NewGuid().ToString("N"));
    private readonly Config _config;
    private readonly WorkQueue _queue;
    private readonly string _workspace;

    public WorkQueueTests()
    {
        _config = new Config(Path.Combine(_root, "home"));
        _config.Initialize();
        _queue = new WorkQueue(_config);
        _workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void UnreadableWorkFileDoesNotHideValidWork()
    {
        var valid = _queue.Add("valid", _workspace);
        File.WriteAllText(_config.WorkFile("corrupt-item"), "{ not json");

        var pending = Assert.Single(_queue.Pending());
        Assert.Equal(valid.Id, pending.Id);
        Assert.Equal(valid.Id, Assert.Single(_queue.List()).Id);
    }

    [Fact]
    public void IncompleteOrMismatchedWorkFileDoesNotHideValidWork()
    {
        var valid = _queue.Add("valid", _workspace);
        File.WriteAllText(_config.WorkFile("missing-prompt"), """
            {"id":"missing-prompt","state":"pending"}
            """);
        File.Copy(_config.WorkFile(valid.Id), _config.WorkFile("wrong-name"));
        var missingState = JsonNode.Parse(File.ReadAllText(_config.WorkFile(valid.Id)))!.AsObject();
        missingState["id"] = "missing-state";
        missingState.Remove("state");
        File.WriteAllText(_config.WorkFile("missing-state"), missingState.ToJsonString());

        Assert.Equal(valid.Id, Assert.Single(_queue.List()).Id);
        Assert.Equal(valid.Id, Assert.Single(_queue.Pending()).Id);
    }

    [Fact]
    public void ClaimAndFinishRecordAttemptsTimestampsAndExitCode()
    {
        var work = _queue.Add("track me", _workspace);

        Assert.True(_queue.TryClaim(work.Id));
        var running = _queue.Get(work.Id)!;
        Assert.Equal(1, running.Attempts);
        Assert.NotNull(running.StartedAt);
        Assert.Null(running.FinishedAt);

        Assert.True(_queue.TryFail(work.Id, "boom", exitCode: 23));
        var failed = _queue.Get(work.Id)!;
        Assert.Equal(WorkState.Failed, failed.State);
        Assert.Equal(23, failed.ExitCode);
        Assert.NotNull(failed.FinishedAt);
    }

    [Fact]
    public void TitleComesFromTheFirstPromptLine()
    {
        var work = _queue.Add("first line" + Environment.NewLine + "details", _workspace);

        Assert.Equal("first line", work.Title);
        Assert.Equal("first line", work.Summary());
    }

    [Fact]
    public void CancellingPendingWorkFailsItWithoutClaiming()
    {
        var work = _queue.Add("never runs", _workspace);

        Assert.Equal(CancelResult.Cancelled, _queue.Cancel(work.Id));

        var stored = _queue.Get(work.Id)!;
        Assert.Equal(WorkState.Failed, stored.State);
        Assert.Equal("Cancelled by user.", stored.Error);
        Assert.Empty(_queue.Pending());
    }

    [Fact]
    public void CancellingRunningWorkOnlyRequestsIt()
    {
        var work = _queue.Add("running", _workspace);
        Assert.True(_queue.TryClaim(work.Id));

        Assert.Equal(CancelResult.Requested, _queue.Cancel(work.Id));

        Assert.Equal(WorkState.Running, _queue.Get(work.Id)!.State);
        Assert.Equal([work.Id], _queue.CancelRequests());
    }

    [Fact]
    public void RecoveringCancelRequestedWorkFailsItInsteadOfRequeueing()
    {
        var work = _queue.Add("interrupted cancel", _workspace);
        Assert.True(_queue.TryClaim(work.Id));
        _queue.Cancel(work.Id);

        Assert.Equal(1, _queue.RecoverRunning());

        var stored = _queue.Get(work.Id)!;
        Assert.Equal(WorkState.Failed, stored.State);
        Assert.Equal("Cancelled by user.", stored.Error);
        Assert.False(stored.CancelRequested);
    }

    [Fact]
    public void RecoveringOtherRunningWorkReturnsItToPending()
    {
        var work = _queue.Add("interrupted", _workspace);
        Assert.True(_queue.TryClaim(work.Id));

        _queue.RecoverRunning();

        Assert.Equal(WorkState.Pending, _queue.Get(work.Id)!.State);
    }

    [Fact]
    public void EditAndDeleteOnlyApplyToPendingWork()
    {
        var work = _queue.Add("before", _workspace);

        Assert.Equal(WorkChange.Done, _queue.Edit(work.Id, "after"));
        Assert.Equal("after", _queue.Get(work.Id)!.Prompt);
        Assert.Equal("after", _queue.Get(work.Id)!.Title);

        Assert.True(_queue.TryClaim(work.Id));
        Assert.Equal(WorkChange.NotPending, _queue.Edit(work.Id, "too late"));
        Assert.Equal(WorkChange.Running, _queue.Delete(work.Id));
        Assert.Equal("after", _queue.Get(work.Id)!.Prompt);
    }

    [Fact]
    public void DeleteRemovesPendingWork()
    {
        var work = _queue.Add("remove me", _workspace);

        Assert.Equal(WorkChange.Done, _queue.Delete(work.Id));
        Assert.Null(_queue.Get(work.Id));
        Assert.Equal(WorkChange.NotFound, _queue.Delete(work.Id));
    }

    [Fact]
    public void DeleteRemovesFinishedWork()
    {
        var work = _queue.Add("done", _workspace);
        Assert.True(_queue.TryClaim(work.Id));
        Assert.True(_queue.TryFail(work.Id, "boom", exitCode: 1));

        Assert.Equal(WorkChange.Done, _queue.Delete(work.Id));
        Assert.Null(_queue.Get(work.Id));
    }
}
