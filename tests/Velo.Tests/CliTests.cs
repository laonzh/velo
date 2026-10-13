using System.Diagnostics;
using System.Text.Json;

namespace Velo.Tests;

[Collection("Sequential")]
public sealed class CliTests
{
    [Fact]
    public async Task AddStoresOneFlatWorkFileWithInternalState()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();

        var add = await harness.RunAsync(workspace, "add", "--", "hello work");
        var id = add.StandardOutput.Trim();
        var path = harness.WorkPath(id);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = document.RootElement;
        Assert.Equal(id, root.GetProperty("id").GetString());
        Assert.Equal("pending", root.GetProperty("state").GetString());
        Assert.Equal("hello work", root.GetProperty("prompt").GetString());
        Assert.False(root.TryGetProperty("title", out _));
        Assert.Equal(workspace, root.GetProperty("workspace").GetString());
        Assert.Equal(
            id + ".json",
            Path.GetFileName(Directory.EnumerateFiles(Path.Combine(harness.VeloHome, "work")).Single()),
            ignoreCase: true);

        var list = await harness.RunAsync(workspace, "list");
        Assert.Contains(id, list.StandardOutput);
        Assert.Contains("pending", list.StandardOutput);
        Assert.Contains("hello work", list.StandardOutput);
    }

    [Fact]
    public async Task RunDrainsWorkAndStoresTerminalState()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var add = await harness.RunAsync(workspace, "add", "--", "run me");
        var id = add.StandardOutput.Trim();

        var run = await harness.RunAsync(workspace, "start", "--once");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("run me", await File.ReadAllTextAsync(Path.Combine(workspace, "codex-ran.txt")));
        Assert.Equal(WorkState.Succeeded, (await GetWorkAsync(harness, id)).State);
        var log = await File.ReadAllTextAsync(Path.Combine(harness.VeloHome, "logs", id + ".log"));
        Assert.Contains("run me", log);
        Assert.Contains("--approve-for-me", log);
    }

    [Fact]
    public async Task NonAsciiPromptReachesCodexUnchanged()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        const string prompt = "中文提示：修改 README";
        await harness.RunAsync(workspace, "add", "--", prompt);

        var run = await harness.RunAsync(workspace, "start", "--once");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(prompt, await File.ReadAllTextAsync(Path.Combine(workspace, "codex-ran.txt")));
    }

    [Fact]
    public async Task LogsCanBeReadWhileWorkIsRunning()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var gate = Path.Combine(harness.VeloHome, "logs-gate");
        var add = await harness.RunAsync(workspace, "add", "--", $"wait:{gate}");
        var id = add.StandardOutput.Trim();

        using var run = harness.StartRun(workspace, concurrency: 1);
        await harness.WaitForFileAsync(gate + ".started");
        try
        {
            var logs = await harness.RunAsync(workspace, "logs", id);

            Assert.Equal(0, logs.ExitCode);
            Assert.Contains("Starting: codex", logs.StandardOutput);
            Assert.Equal(WorkState.Running, (await GetWorkAsync(harness, id)).State);
            Assert.False(run.HasExited);
        }
        finally
        {
            await File.WriteAllTextAsync(gate, "release");
            await harness.WaitForExitAsync(run);
        }

        var completedLogs = await harness.RunAsync(workspace, "logs", id);
        Assert.Equal(0, completedLogs.ExitCode);
        Assert.Contains("Starting: codex", completedLogs.StandardOutput);
        Assert.Equal(WorkState.Succeeded, (await GetWorkAsync(harness, id)).State);
    }

    [Fact]
    public async Task FailedWorkCanBeRetriedAndLogsContainError()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var add = await harness.RunAsync(workspace, "add", "--", "fail:expected failure");
        var id = add.StandardOutput.Trim();

        var run = await harness.RunAsync(workspace, "start", "--once");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(WorkState.Failed, (await GetWorkAsync(harness, id)).State);
        Assert.Contains("expected failure", (await GetWorkAsync(harness, id)).Error);
        Assert.Contains(
            "expected failure",
            await File.ReadAllTextAsync(Path.Combine(harness.VeloHome, "logs", id + ".log")));

        Assert.Equal(0, (await harness.RunAsync(workspace, "retry", id)).ExitCode);
        Assert.Equal(WorkState.Pending, (await GetWorkAsync(harness, id)).State);
        Assert.Null((await GetWorkAsync(harness, id)).Error);

        var filtered = await harness.RunAsync(workspace, "list", "failed");
        Assert.Contains("No work.", filtered.StandardOutput);
    }

    [Fact]
    public async Task FailedWorkKeepsTheFirstErrorLineAsItsSummary()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var add = await harness.RunAsync(
            workspace,
            "add",
            "--",
            "fail:root cause" + Environment.NewLine + "For more information, try '--help'.");
        var id = add.StandardOutput.Trim();

        await harness.RunAsync(workspace, "start", "--once");

        Assert.Equal("root cause", (await GetWorkAsync(harness, id)).Error);
    }

    [Fact]
    public async Task FailedWorkSkipsCodexLogRecordsWhenChoosingItsSummary()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var add = await harness.RunAsync(workspace, "add", "--", "warn:real failure");
        var id = add.StandardOutput.Trim();

        await harness.RunAsync(workspace, "start", "--once");

        Assert.Equal("real failure", (await GetWorkAsync(harness, id)).Error);
    }

    [Fact]
    public async Task CodexExitingBeforeReadingPromptFailsTheWorkWithoutHanging()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var oldHome = Environment.GetEnvironmentVariable("VELO_HOME");
        var oldBehavior = Environment.GetEnvironmentVariable("VELO_FAKE_CODEX");
        var oldPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("VELO_HOME", harness.VeloHome);
            Environment.SetEnvironmentVariable("VELO_FAKE_CODEX", "exit-early");
            Environment.SetEnvironmentVariable("PATH", harness.FakeBin + Path.PathSeparator + oldPath);

            var config = Config.Load();
            config.Initialize();
            var queue = new WorkQueue(config);
            // Larger than a pipe buffer, so the write cannot finish before codex exits.
            var queuedWork = queue.Add(new string('x', 4 * 1024 * 1024), workspace);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await new Orchestrator(queue, new AgentRunner(config).RunAsync, config, concurrency: 1)
                .RunAsync(cancellation.Token);

            var storedWork = queue.Get(queuedWork.Id);
            Assert.NotNull(storedWork);
            Assert.Equal(WorkState.Failed, storedWork.State);
            Assert.NotNull(storedWork.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VELO_HOME", oldHome);
            Environment.SetEnvironmentVariable("VELO_FAKE_CODEX", oldBehavior);
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    [Fact]
    public async Task InvalidConcurrencyValueReturnsUsageError()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var oldConcurrency = Environment.GetEnvironmentVariable("VELO_CONCURRENCY");

        try
        {
            Environment.SetEnvironmentVariable("VELO_CONCURRENCY", "0");
            using var run = harness.StartRun(workspace, concurrency: null);
            await harness.WaitForExitAsync(run);

            Assert.Equal(1, run.ExitCode);
            Assert.Contains(
                "VELO_CONCURRENCY must be a positive integer.",
                await run.StandardError.ReadToEndAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("VELO_CONCURRENCY", oldConcurrency);
        }
    }

    [Fact]
    public async Task ManagedWorktreesRunConcurrentlyAndUsePredictablePaths()
    {
        await using var harness = new CliFixture();
        var repository = harness.Workspace("repository");
        await GitAsync(repository, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "source");
        await GitAsync(repository, "add", ".");
        await GitAsync(repository, "-c", "user.email=velo@example.com", "-c", "user.name=Velo", "commit", "-m", "init");

        var gate1 = Path.Combine(harness.VeloHome, "worktree-gate-1");
        var gate2 = Path.Combine(harness.VeloHome, "worktree-gate-2");
        var id1 = (await harness.RunAsync(repository, "add", "--worktree", "--", $"wait:{gate1}"))
            .StandardOutput.Trim();
        var id2 = (await harness.RunAsync(repository, "add", "--worktree", "--", $"wait:{gate2}"))
            .StandardOutput.Trim();
        var workspace1 = (await GetWorkAsync(harness, id1)).Workspace;
        var workspace2 = (await GetWorkAsync(harness, id2)).Workspace;
        Assert.Equal(Path.Combine(harness.VeloHome, "workspaces", id1), workspace1);
        Assert.Equal(Path.Combine(harness.VeloHome, "workspaces", id2), workspace2);
        Assert.NotEqual(workspace1, workspace2);

        using var run = harness.StartRun(repository, concurrency: 2);
        await harness.WaitForFileAsync(gate1 + ".started");
        await harness.WaitForFileAsync(gate2 + ".started");
        Assert.False(run.HasExited);
        await File.WriteAllTextAsync(gate1, "release");
        await File.WriteAllTextAsync(gate2, "release");
        await harness.WaitForExitAsync(run);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(WorkState.Succeeded, (await GetWorkAsync(harness, id1)).State);
        Assert.Equal(WorkState.Succeeded, (await GetWorkAsync(harness, id2)).State);
    }

    [Fact]
    public async Task AddWorktreeRunsAtRootFromSourceHeadAndPreservesSource()
    {
        await using var harness = new CliFixture();
        var repository = harness.Workspace("repository");
        await GitAsync(repository, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "source");
        await GitAsync(repository, "add", ".");
        await GitAsync(repository, "-c", "user.email=velo@example.com", "-c", "user.name=Velo", "commit", "-m", "init");
        var sourceHead = await GitAsync(repository, "rev-parse", "HEAD");

        var id = (await harness.RunAsync(repository, "add", "--worktree", "--", "worktree execution"))
            .StandardOutput.Trim();
        var workspace = (await GetWorkAsync(harness, id)).Workspace;

        Assert.Equal(Path.Combine(harness.VeloHome, "workspaces", id), workspace);

        var run = await harness.RunAsync(repository, "start", "--once");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(WorkState.Succeeded, (await GetWorkAsync(harness, id)).State);

        Assert.Equal("velo/" + id, await GitAsync(workspace, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal(sourceHead, await GitAsync(workspace, "rev-parse", "HEAD"));
        Assert.Equal(
            "worktree execution",
            await File.ReadAllTextAsync(Path.Combine(workspace, "codex-ran.txt")));
        Assert.False(File.Exists(Path.Combine(repository, "codex-ran.txt")));
        Assert.Equal(string.Empty, await GitAsync(repository, "status", "--porcelain"));
        Assert.Equal("main", await GitAsync(repository, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.True(Directory.Exists(workspace));
    }

    [Fact]
    public async Task WorktreeResultCanBeReviewedWithoutChangingTheCheckout()
    {
        await using var harness = new CliFixture();
        var repository = harness.Workspace("repository");
        await TestGit.InitRepositoryAsync(repository);
        var id = (await harness.RunAsync(repository, "add", "--worktree", "--", "review me")).StandardOutput.Trim();
        Assert.Equal(0, (await harness.RunAsync(repository, "start", "--once")).ExitCode);
        var workspace = (await GetWorkAsync(harness, id)).Workspace;

        var diff = await harness.RunAsync(repository, "diff", id);

        Assert.Equal(0, diff.ExitCode);
        Assert.Contains("codex-ran.txt", diff.StandardOutput);
        Assert.Contains("+review me", diff.StandardOutput);
        // The review uses a temporary index, so the worktree's own index and status are unchanged.
        Assert.Equal("?? codex-ran.txt", await GitAsync(workspace, "status", "--porcelain"));
        Assert.Equal(string.Empty, await GitAsync(workspace, "diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task DiscardRemovesTheWorktreeAndBranchAndKeepsTheRecord()
    {
        await using var harness = new CliFixture();
        var repository = harness.Workspace("repository");
        await TestGit.InitRepositoryAsync(repository);
        var id = (await harness.RunAsync(repository, "add", "--worktree", "--", "discard me")).StandardOutput.Trim();
        Assert.Equal(0, (await harness.RunAsync(repository, "start", "--once")).ExitCode);
        var workspace = (await GetWorkAsync(harness, id)).Workspace;

        var discard = await harness.RunAsync(repository, "discard", id);

        Assert.Equal(0, discard.ExitCode);
        Assert.False(Directory.Exists(workspace));
        Assert.Equal(string.Empty, await GitAsync(repository, "branch", "--list", "velo/" + id));
        Assert.DoesNotContain(id, await GitAsync(repository, "worktree", "list", "--porcelain"));
        var work = await GetWorkAsync(harness, id);
        Assert.Equal(WorkState.Succeeded, work.State);
        Assert.True(work.Discarded);
        Assert.True(File.Exists(Path.Combine(harness.VeloHome, "logs", id + ".log")));

        var diffAgain = await harness.RunAsync(repository, "diff", id);
        Assert.Equal(1, diffAgain.ExitCode);
        Assert.Contains("Only succeeded work with a worktree", diffAgain.StandardError);
        var discardAgain = await harness.RunAsync(repository, "discard", id);
        Assert.Equal(1, discardAgain.ExitCode);
        Assert.Contains("Only succeeded work with a worktree", discardAgain.StandardError);
        Assert.Equal(0, (await harness.RunAsync(repository, "delete", id)).ExitCode);
    }

    [Fact]
    public async Task AcceptAppliesTheChangesToTheRepositoryWithoutCommitting()
    {
        await using var harness = new CliFixture();
        var repository = harness.Workspace("repository");
        await TestGit.InitRepositoryAsync(repository);
        var head = await GitAsync(repository, "rev-parse", "HEAD");
        var id = (await harness.RunAsync(repository, "add", "--worktree", "--", "accept me")).StandardOutput.Trim();
        Assert.Equal(0, (await harness.RunAsync(repository, "start", "--once")).ExitCode);
        var workspace = (await GetWorkAsync(harness, id)).Workspace;

        var accept = await harness.RunAsync(repository, "accept", id);
        Assert.Equal(0, accept.ExitCode);
        Assert.Equal("accept me", await File.ReadAllTextAsync(Path.Combine(repository, "codex-ran.txt")));
        Assert.Equal("?? codex-ran.txt", await GitAsync(repository, "status", "--porcelain"));
        Assert.Equal(head, await GitAsync(repository, "rev-parse", "HEAD"));
        Assert.True((await GetWorkAsync(harness, id)).Accepted);
        Assert.True(Directory.Exists(workspace));

        var again = await harness.RunAsync(repository, "accept", id);
        Assert.Equal(1, again.ExitCode);
        Assert.Contains("already accepted", again.StandardError);

        // Discarding an accepted result is still allowed; it only removes the worktree.
        Assert.Equal(0, (await harness.RunAsync(repository, "discard", id)).ExitCode);
        Assert.False(Directory.Exists(workspace));
        Assert.Equal("accept me", await File.ReadAllTextAsync(Path.Combine(repository, "codex-ran.txt")));
    }

    [Fact]
    public async Task AcceptRefusesChangesThatDoNotApplyAndChangesNothing()
    {
        await using var harness = new CliFixture();
        var repository = harness.Workspace("repository");
        await TestGit.InitRepositoryAsync(repository);
        var id = (await harness.RunAsync(repository, "add", "--worktree", "--", "conflicting")).StandardOutput.Trim();
        Assert.Equal(0, (await harness.RunAsync(repository, "start", "--once")).ExitCode);
        await File.WriteAllTextAsync(Path.Combine(repository, "codex-ran.txt"), "mine");

        var accept = await harness.RunAsync(repository, "accept", id);
        Assert.Equal(1, accept.ExitCode);
        Assert.Contains("nothing was changed", accept.StandardError);
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(repository, "codex-ran.txt")));
        Assert.Equal("?? codex-ran.txt", await GitAsync(repository, "status", "--porcelain"));
        Assert.False((await GetWorkAsync(harness, id)).Accepted);
        Assert.Equal(0, (await harness.RunAsync(repository, "diff", id)).ExitCode);
    }

    [Fact]
    public async Task AcceptAppliesTheWholePatchEvenWhenItsPreviewIsCut()
    {
        await using var harness = new CliFixture();
        var repository = harness.Workspace("repository");
        await TestGit.InitRepositoryAsync(repository);
        var id = (await harness.RunAsync(repository, "add", "--worktree", "--", "large change")).StandardOutput.Trim();
        Assert.Equal(0, (await harness.RunAsync(repository, "start", "--once")).ExitCode);
        var workspace = (await GetWorkAsync(harness, id)).Workspace;
        var large = new string('a', 600_000) + "\n";
        await File.WriteAllTextAsync(Path.Combine(workspace, "large.txt"), large);

        // The preview is cut off at 512 KiB; the accepted patch must not be.
        var diff = await harness.RunAsync(repository, "diff", id);
        Assert.Contains("patch cut off", diff.StandardOutput);

        Assert.Equal(0, (await harness.RunAsync(repository, "accept", id)).ExitCode);
        Assert.Equal(large, await File.ReadAllTextAsync(Path.Combine(repository, "large.txt")));
    }

    [Fact]
    public async Task WorkWithoutItsOwnWorktreeCannotBeReviewedOrDiscarded()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var id = (await harness.RunAsync(workspace, "add", "--", "plain work")).StandardOutput.Trim();
        Assert.Equal(0, (await harness.RunAsync(workspace, "start", "--once")).ExitCode);

        var diff = await harness.RunAsync(workspace, "diff", id);
        var discard = await harness.RunAsync(workspace, "discard", id);

        Assert.Equal(1, diff.ExitCode);
        Assert.Contains("Only succeeded work with a worktree", diff.StandardError);
        Assert.Equal(1, discard.ExitCode);
        Assert.Contains("Only succeeded work with a worktree", discard.StandardError);
        Assert.False((await GetWorkAsync(harness, id)).Discarded);
    }

    [Fact]
    public async Task DiscardStillFinishesWhenTheWorktreeDirectoryWasRemovedByHand()
    {
        await using var harness = new CliFixture();
        var repository = harness.Workspace("repository");
        await TestGit.InitRepositoryAsync(repository);
        var id = (await harness.RunAsync(repository, "add", "--worktree", "--", "manual removal")).StandardOutput.Trim();
        Assert.Equal(0, (await harness.RunAsync(repository, "start", "--once")).ExitCode);
        var workspace = (await GetWorkAsync(harness, id)).Workspace;
        Directory.Delete(workspace, recursive: true);

        var diff = await harness.RunAsync(repository, "diff", id);
        var discard = await harness.RunAsync(repository, "discard", id);

        Assert.Equal(1, diff.ExitCode);
        Assert.Contains("The worktree is missing", diff.StandardError);
        Assert.Equal(0, discard.ExitCode);
        Assert.Equal(string.Empty, await GitAsync(repository, "branch", "--list", "velo/" + id));
        Assert.True((await GetWorkAsync(harness, id)).Discarded);
    }

    [Fact]
    public async Task OldStateDirectoriesAreIgnoredWithoutMigration()
    {
        await using var harness = new CliFixture();
        var legacyDirectory = Path.Combine(harness.VeloHome, "work", "pending");
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(legacyDirectory, "legacy.json"),
            """{"id":"legacy","state":"pending","prompt":"old","workspace":"old","createdAt":"2020-01-01T00:00:00Z"}""");

        var run = await harness.RunAsync(harness.RootPath, "start", "--once");
        var list = await harness.RunAsync(harness.RootPath, "list");
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("No work.", list.StandardOutput);
        Assert.True(File.Exists(Path.Combine(legacyDirectory, "legacy.json")));
    }

    [Fact]
    public async Task CancellationDuringBlockedStandardInputRequeuesWorkAndStopsOrchestrator()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var marker = Path.Combine(harness.VeloHome, "blocked-codex.pid");
        var oldHome = Environment.GetEnvironmentVariable("VELO_HOME");
        var oldBehavior = Environment.GetEnvironmentVariable("VELO_FAKE_CODEX");
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        using var cancellation = new CancellationTokenSource();
        Task? orchestratorRun = null;
        var processId = 0;

        try
        {
            Environment.SetEnvironmentVariable("VELO_HOME", harness.VeloHome);
            Environment.SetEnvironmentVariable("VELO_FAKE_CODEX", "block-stdin:" + marker);
            Environment.SetEnvironmentVariable(
                "PATH",
                harness.FakeBin + Path.PathSeparator + oldPath);

            var config = Config.Load();
            config.Initialize();
            var queue = new WorkQueue(config);
            var queuedWork = queue.Add(new string('x', 4 * 1024 * 1024), workspace);

            orchestratorRun = new Orchestrator(
                queue,
                new AgentRunner(config).RunAsync,
                config,
                concurrency: 1).RunAsync(cancellation.Token);
            await harness.WaitForFileAsync(marker);
            processId = int.Parse(await harness.ReadAllTextAsync(marker));

            cancellation.Cancel();
            await Task.WhenAny(
                orchestratorRun,
                Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.True(orchestratorRun.IsCompleted, "Orchestrator remained blocked on standard input.");
            await orchestratorRun;

            var storedWork = queue.Get(queuedWork.Id);
            Assert.NotNull(storedWork);
            Assert.Equal(WorkState.Pending, storedWork.State);
            Assert.False(IsProcessRunning(processId));
        }
        finally
        {
            cancellation.Cancel();
            if (processId > 0) KillProcessTree(processId);
            if (orchestratorRun is not null)
            {
                await Task.WhenAny(orchestratorRun, Task.Delay(TimeSpan.FromSeconds(2)));
            }

            Environment.SetEnvironmentVariable("VELO_HOME", oldHome);
            Environment.SetEnvironmentVariable("VELO_FAKE_CODEX", oldBehavior);
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    [Fact]
    public async Task CancellingRunningWorkStopsCodexAndRecordsCancellation()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var gate = Path.Combine(harness.VeloHome, "cancel-gate");
        var id = (await harness.RunAsync(workspace, "add", "--", $"wait:{gate}")).StandardOutput.Trim();

        using var run = harness.StartRun(workspace, concurrency: 1);
        await harness.WaitForFileAsync(gate + ".started");
        var cancel = await harness.RunAsync(workspace, "cancel", id);
        Assert.Equal(0, cancel.ExitCode);
        await harness.WaitForExitAsync(run);

        Assert.Equal(0, run.ExitCode);
        Assert.False(File.Exists(gate + ".completed"));
        var work = await GetWorkAsync(harness, id);
        Assert.Equal(WorkState.Failed, work.State);
        Assert.Equal("Cancelled by user.", work.Error);
    }

    [Fact]
    public async Task StopEndsABackgroundStartAndReturnsRunningWorkToPending()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var gate = Path.Combine(harness.VeloHome, "stop-gate");
        var pidFile = Path.Combine(harness.VeloHome, "start.pid");
        var processId = 0;
        try
        {
            Assert.Equal(0, (await harness.RunAsync(workspace, "start", "-d")).ExitCode);
            await harness.WaitForFileAsync(pidFile);
            processId = int.Parse(await harness.ReadAllTextAsync(pidFile));
            var id = (await harness.RunAsync(workspace, "add", "--", $"wait:{gate}")).StandardOutput.Trim();
            await harness.WaitForFileAsync(gate + ".started");
            var codexProcessId = int.Parse(await File.ReadAllTextAsync(gate + ".started"));

            var stop = await harness.RunAsync(workspace, "stop");
            Assert.Equal(0, stop.ExitCode);
            Assert.Contains($"Stopped velo start (pid {processId}).", stop.StandardOutput);
            Assert.False(IsProcessRunning(processId));
            Assert.False(IsProcessRunning(codexProcessId));
            Assert.False(File.Exists(gate + ".completed"));
            Assert.Equal(WorkState.Pending, (await GetWorkAsync(harness, id)).State);
        }
        finally
        {
            if (processId > 0) KillProcessTree(processId);
        }
    }

    [Fact]
    public async Task StopEndsAForegroundStart()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        using var run = harness.StartRun(workspace, concurrency: 1);
        await harness.WaitForFileAsync(Path.Combine(harness.VeloHome, "start.pid"));

        var stop = await harness.RunAsync(workspace, "stop");
        Assert.Equal(0, stop.ExitCode);
        await harness.WaitForExitAsync(run);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public async Task StopWithoutAStartSaysNothingIsRunning()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();

        var stop = await harness.RunAsync(workspace, "stop");
        Assert.Equal(0, stop.ExitCode);
        Assert.Contains("No velo start is running.", stop.StandardOutput);
    }

    [Fact]
    public async Task HelpExposesOnlyTheSimplifiedForegroundSurface()
    {
        await using var harness = new CliFixture();
        var help = await harness.RunAsync(harness.RootPath, "help");
        Assert.Contains("velo add", help.StandardOutput);
        Assert.Contains("velo start [--once] [-d] [--port N]", help.StandardOutput);
        Assert.Contains("velo diff <id>", help.StandardOutput);
        Assert.Contains("velo stop", help.StandardOutput);
        Assert.Contains("velo accept <id>", help.StandardOutput);
        Assert.Contains("velo discard <id>", help.StandardOutput);
        Assert.DoesNotContain("velo run", help.StandardOutput);
        Assert.DoesNotContain("velo serve", help.StandardOutput);
        Assert.DoesNotContain("velo status", help.StandardOutput);
    }

    [Fact]
    public async Task StartRejectsUnexpectedArguments()
    {
        await using var harness = new CliFixture();
        var result = await harness.RunAsync(harness.RootPath, "start", "--bogus");
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Usage: velo start", result.StandardError);
    }

    [Fact]
    public async Task RunCommandIsRemoved()
    {
        await using var harness = new CliFixture();
        var result = await harness.RunAsync(harness.RootPath, "run");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown command: run", result.StandardError);
    }

    [Fact]
    public async Task StartWithoutOnceKeepsWatchingForNewWorkUntilInterrupted()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var pidFile = Path.Combine(harness.VeloHome, "start.pid");
        using var service = harness.StartWatching(workspace, concurrency: 1);
        try
        {
            await harness.WaitForFileAsync(pidFile);
            Assert.Equal(service.Id, int.Parse(await harness.ReadAllTextAsync(pidFile)));

            var id = (await harness.RunAsync(workspace, "add", "--", "watched work")).StandardOutput.Trim();
            await WaitForStateAsync(harness, id, WorkState.Succeeded);
            Assert.False(service.HasExited);
        }
        finally
        {
            KillProcessTree(service.Id);
        }
    }

    [Fact]
    public async Task DetachedStartRunsInTheBackgroundAndRefusesDuplicates()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var pidFile = Path.Combine(harness.VeloHome, "start.pid");
        var processId = 0;
        try
        {
            var start = await harness.RunAsync(workspace, "start", "-d");
            Assert.Equal(0, start.ExitCode);
            await harness.WaitForFileAsync(pidFile);
            processId = int.Parse(await harness.ReadAllTextAsync(pidFile));
            Assert.Contains(processId.ToString(), start.StandardOutput);
            Assert.True(IsProcessRunning(processId));

            var id = (await harness.RunAsync(workspace, "add", "--", "background work")).StandardOutput.Trim();
            await WaitForStateAsync(harness, id, WorkState.Succeeded);

            var duplicate = await harness.RunAsync(workspace, "start", "-d");
            Assert.Equal(1, duplicate.ExitCode);
            Assert.Contains($"already running (pid {processId})", duplicate.StandardError);

            var log = await harness.ReadAllTextAsync(Path.Combine(harness.VeloHome, "start.log"));
            Assert.Contains("succeeded " + id, log);
        }
        finally
        {
            if (processId > 0) KillProcessTree(processId);
        }
    }

    [Fact]
    public async Task DetachedStartMovesAnOversizedLogAside()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var log = Path.Combine(harness.VeloHome, "start.log");
        var oversized = new string('x', 1024 * 1024 + 1);
        await File.WriteAllTextAsync(log, oversized);
        var pidFile = Path.Combine(harness.VeloHome, "start.pid");
        var processId = 0;
        try
        {
            var start = await harness.RunAsync(workspace, "start", "-d");
            Assert.Equal(0, start.ExitCode);
            await harness.WaitForFileAsync(pidFile);
            processId = int.Parse(await harness.ReadAllTextAsync(pidFile));

            // The background process rotates the log itself, so wait for both files to settle.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline
                && !(File.Exists(log + ".1") && File.Exists(log) && new FileInfo(log).Length < oversized.Length))
            {
                await Task.Delay(25);
            }

            Assert.Equal(oversized.Length, new FileInfo(log + ".1").Length);
            Assert.True(new FileInfo(log).Length < oversized.Length);
        }
        finally
        {
            if (processId > 0) KillProcessTree(processId);
        }
    }

    [Fact]
    public async Task DeleteRemovesFinishedWorkAndItsLog()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var id = (await harness.RunAsync(workspace, "add", "--", "finished work")).StandardOutput.Trim();
        Assert.Equal(0, (await harness.RunAsync(workspace, "start", "--once")).ExitCode);
        Assert.Equal(WorkState.Succeeded, (await GetWorkAsync(harness, id)).State);
        var logPath = Path.Combine(harness.VeloHome, "logs", id + ".log");
        Assert.True(File.Exists(logPath));

        Assert.Equal(0, (await harness.RunAsync(workspace, "delete", id)).ExitCode);
        Assert.False(File.Exists(harness.WorkPath(id)));
        Assert.False(File.Exists(logPath));
    }

    private static async Task WaitForStateAsync(CliFixture harness, string id, WorkState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if ((await GetWorkAsync(harness, id)).State == state) return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Timed out waiting for {id} to become {state.Text()}.");
    }

    [Fact]
    public async Task ForcedKillOfBackgroundStartAlsoStopsCodex()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var gate = Path.Combine(harness.VeloHome, "forced-kill-gate");
        var pidFile = Path.Combine(harness.VeloHome, "start.pid");
        var processId = 0;
        try
        {
            Assert.Equal(0, (await harness.RunAsync(workspace, "start", "-d")).ExitCode);
            await harness.WaitForFileAsync(pidFile);
            processId = int.Parse(await harness.ReadAllTextAsync(pidFile));
            await harness.RunAsync(workspace, "add", "--", $"wait:{gate}");
            await harness.WaitForFileAsync(gate + ".started");

            // Kill only velo, not its tree: Codex must go down with the job, not with a tree kill.
            Process.GetProcessById(processId).Kill();
            for (var attempt = 0; attempt < 100 && IsProcessRunning(processId); attempt++)
                await Task.Delay(50);
            await File.WriteAllTextAsync(gate, "release");
            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.False(File.Exists(gate + ".completed"));
        }
        finally
        {
            if (processId > 0) KillProcessTree(processId);
        }
    }

    [Fact]
    public async Task CancellingWithoutAStartRunningSaysItTakesEffectLater()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var gate = Path.Combine(harness.VeloHome, "stale-cancel-gate");
        var id = (await harness.RunAsync(workspace, "add", "--", $"wait:{gate}")).StandardOutput.Trim();

        using var run = harness.StartRun(workspace, concurrency: 1);
        await harness.WaitForFileAsync(gate + ".started");
        run.Kill(entireProcessTree: true);
        await harness.WaitForExitAsync(run);

        var cancel = await harness.RunAsync(workspace, "cancel", id);
        Assert.Equal(0, cancel.ExitCode);
        Assert.Contains("no velo start is running", cancel.StandardOutput);

        Assert.Equal(0, (await harness.RunAsync(workspace, "start", "--once")).ExitCode);
        var work = await GetWorkAsync(harness, id);
        Assert.Equal(WorkState.Failed, work.State);
        Assert.Equal("Cancelled by user.", work.Error);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void KillProcessTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
        }
    }

    private static async Task<WorkItem> GetWorkAsync(CliFixture harness, string id) =>
        JsonSerializer.Deserialize(
            await harness.ReadAllTextAsync(harness.WorkPath(id)),
            VeloJsonContext.Default.WorkItem)!;

    private static Task<string> GitAsync(
        string workingDirectory,
        params string[] arguments) => TestGit.RunAsync(workingDirectory, arguments);

}

