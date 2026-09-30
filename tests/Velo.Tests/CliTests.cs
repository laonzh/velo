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

        var run = await harness.RunAsync(workspace, "run");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("run me", await File.ReadAllTextAsync(Path.Combine(workspace, "codex-ran.txt")));
        Assert.Equal(WorkState.Succeeded, (await GetWorkAsync(harness, id)).State);
        var log = await File.ReadAllTextAsync(Path.Combine(harness.VeloHome, "logs", id + ".log"));
        Assert.Contains("run me", log);
        Assert.Contains("--approve-for-me", log);
        Assert.DoesNotContain("--sandbox", log);
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

        var run = await harness.RunAsync(workspace, "run");
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

        await harness.RunAsync(workspace, "run");

        Assert.Equal("root cause", (await GetWorkAsync(harness, id)).Error);
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

        var run = await harness.RunAsync(repository, "run");
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
    public async Task OldStateDirectoriesAreIgnoredWithoutMigration()
    {
        await using var harness = new CliFixture();
        var legacyDirectory = Path.Combine(harness.VeloHome, "work", "pending");
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(legacyDirectory, "legacy.json"),
            """{"id":"legacy","state":"pending","prompt":"old","workspace":"old","createdAt":"2020-01-01T00:00:00Z"}""");

        var run = await harness.RunAsync(harness.RootPath, "run");
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
    public async Task HelpExposesOnlyTheSimplifiedForegroundSurface()
    {
        await using var harness = new CliFixture();
        var help = await harness.RunAsync(harness.RootPath, "help");
        Assert.Contains("velo add", help.StandardOutput);
        Assert.Contains("velo run", help.StandardOutput);
        Assert.DoesNotContain("velo start", help.StandardOutput);
        Assert.DoesNotContain("velo stop", help.StandardOutput);
        Assert.DoesNotContain("velo serve", help.StandardOutput);
        Assert.DoesNotContain("velo status", help.StandardOutput);
    }

    [Fact]
    public async Task RunRejectsUnexpectedArguments()
    {
        await using var harness = new CliFixture();
        var result = await harness.RunAsync(harness.RootPath, "run", "--bogus");
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Usage: velo run", result.StandardError);
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
            await File.ReadAllBytesAsync(harness.WorkPath(id)),
            VeloJsonContext.Default.WorkItem)!;

    private static async Task<string> GitAsync(
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(await error);
        return (await output).Trim();
    }

}

