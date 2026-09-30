namespace Velo.Tests;

[Collection("Sequential")]
public sealed class OrchestratorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RecordsAgentOutcomes()
    {
        using var context = new TestContext();
        var workspace = context.Workspace("workspace");
        var succeeded = context.Queue.Add("succeed", workspace);
        var failed = context.Queue.Add("fail", workspace);
        var crashed = context.Queue.Add("crash", workspace);

        Task<RunResult> RunAgent(WorkItem work, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return work.Prompt switch
            {
                "succeed" => Task.FromResult(new RunResult(0, null)),
                "fail" => Task.FromResult(new RunResult(23, "expected failure")),
                _ => Task.FromException<RunResult>(new InvalidOperationException("agent crashed"))
            };
        }

        await context.RunAsync(RunAgent, concurrency: 1);

        Assert.Equal(WorkState.Succeeded, context.Queue.Get(succeeded.Id)!.State);
        Assert.Equal(WorkState.Failed, context.Queue.Get(failed.Id)!.State);
        Assert.Equal("expected failure", context.Queue.Get(failed.Id)!.Error);
        Assert.Equal(WorkState.Failed, context.Queue.Get(crashed.Id)!.State);
        Assert.Equal("agent crashed", context.Queue.Get(crashed.Id)!.Error);
    }

    [Fact]
    public async Task RunsSeparateWorkspacesConcurrently()
    {
        using var context = new TestContext();
        var first = context.Queue.Add("first", context.Workspace("one"));
        var second = context.Queue.Add("second", context.Workspace("two"));
        var firstStarted = Signal();
        var secondStarted = Signal();
        var release = Signal();

        async Task<RunResult> RunAgent(WorkItem work, CancellationToken cancellationToken)
        {
            (work.Id == first.Id ? firstStarted : secondStarted).TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new RunResult(0, null);
        }

        var run = context.RunAsync(RunAgent, concurrency: 2);
        await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(Timeout);
        Assert.False(run.IsCompleted);

        release.SetResult();
        await run.WaitAsync(Timeout);
        Assert.Equal(WorkState.Succeeded, context.Queue.Get(first.Id)!.State);
        Assert.Equal(WorkState.Succeeded, context.Queue.Get(second.Id)!.State);
    }

    [Fact]
    public async Task SerializesWorkInTheSameCheckout()
    {
        using var context = new TestContext();
        var repository = context.Workspace("repository");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var firstWorkspace = Path.Combine(repository, "one");
        var secondWorkspace = Path.Combine(repository, "two");
        Directory.CreateDirectory(firstWorkspace);
        Directory.CreateDirectory(secondWorkspace);
        var first = context.Queue.Add("first", firstWorkspace);
        var second = context.Queue.Add("second", secondWorkspace);
        var firstStarted = Signal();
        var secondStarted = Signal();
        var releaseFirst = Signal();
        var releaseSecond = Signal();

        async Task<RunResult> RunAgent(WorkItem work, CancellationToken cancellationToken)
        {
            if (work.Id == first.Id)
            {
                firstStarted.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                secondStarted.SetResult();
                await releaseSecond.Task.WaitAsync(cancellationToken);
            }
            return new RunResult(0, null);
        }

        var run = context.RunAsync(RunAgent, concurrency: 2);
        await firstStarted.Task.WaitAsync(Timeout);
        await Task.Delay(100);
        Assert.False(secondStarted.Task.IsCompleted);

        releaseFirst.SetResult();
        await secondStarted.Task.WaitAsync(Timeout);
        releaseSecond.SetResult();
        await run.WaitAsync(Timeout);

        Assert.Equal(WorkState.Succeeded, context.Queue.Get(first.Id)!.State);
        Assert.Equal(WorkState.Succeeded, context.Queue.Get(second.Id)!.State);
    }

    [Fact]
    public async Task HonorsTheConcurrencyLimit()
    {
        using var context = new TestContext();
        var first = context.Queue.Add("first", context.Workspace("one"));
        var second = context.Queue.Add("second", context.Workspace("two"));
        var firstStarted = Signal();
        var secondStarted = Signal();
        var releaseFirst = Signal();

        async Task<RunResult> RunAgent(WorkItem work, CancellationToken cancellationToken)
        {
            if (work.Id == first.Id)
            {
                firstStarted.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                secondStarted.SetResult();
            }
            return new RunResult(0, null);
        }

        var run = context.RunAsync(RunAgent, concurrency: 1);
        await firstStarted.Task.WaitAsync(Timeout);
        await Task.Delay(100);
        Assert.False(secondStarted.Task.IsCompleted);

        releaseFirst.SetResult();
        await secondStarted.Task.WaitAsync(Timeout);
        await run.WaitAsync(Timeout);
        Assert.Equal(WorkState.Succeeded, context.Queue.Get(second.Id)!.State);
    }

    [Fact]
    public async Task AllowsOnlyOneRunForPhysicalHomeAliases()
    {
        using var context = new TestContext();
        context.Queue.Add("first", context.Workspace("workspace"));
        var started = Signal();
        var release = Signal();

        async Task<RunResult> BlockingAgent(WorkItem work, CancellationToken cancellationToken)
        {
            started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new RunResult(0, null);
        }

        var firstRun = context.RunAsync(BlockingAgent, concurrency: 1);
        await started.Task.WaitAsync(Timeout);

        var aliasConfig = new Config(Path.Combine(context.Config.Home, "alias", ".."));
        var secondRun = new Orchestrator(
            new WorkQueue(aliasConfig),
            (_, _) => Task.FromResult(new RunResult(0, null)),
            aliasConfig,
            concurrency: 1);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondRun.RunAsync(CancellationToken.None));
        Assert.Contains("Another velo run", error.Message);

        release.SetResult();
        await firstRun.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ReleasesRunLockAfterCompletion()
    {
        using var context = new TestContext();
        var workspace = context.Workspace("workspace");
        var first = context.Queue.Add("first", workspace);
        Task<RunResult> Succeed(WorkItem _, CancellationToken cancellationToken) =>
            Task.FromResult(new RunResult(0, null));

        await context.RunAsync(Succeed, concurrency: 1);
        var second = context.Queue.Add("second", workspace);
        await context.RunAsync(Succeed, concurrency: 1);

        Assert.Equal(WorkState.Succeeded, context.Queue.Get(first.Id)!.State);
        Assert.Equal(WorkState.Succeeded, context.Queue.Get(second.Id)!.State);
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TestContext : IDisposable
    {
        public TestContext()
        {
            Root = Path.Combine(Path.GetTempPath(), "Velo.Tests", Guid.NewGuid().ToString("N"));
            Config = new Config(Path.Combine(Root, "home"));
            Config.Initialize();
            Queue = new WorkQueue(Config);
        }

        public string Root { get; }
        public Config Config { get; }
        public WorkQueue Queue { get; }

        public string Workspace(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public Task RunAsync(
            Func<WorkItem, CancellationToken, Task<RunResult>> runAgent,
            int concurrency) =>
            new Orchestrator(Queue, runAgent, Config, concurrency)
                .RunAsync(CancellationToken.None);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}