using System.Diagnostics;
using System.Text.Json;

namespace Velo.Tests;

public sealed class CheckoutConcurrencyTests
{
    [Fact]
    public async Task SameCheckoutSubdirectories_DoNotOverlap()
    {
        var harness = new VeloCliHarness();
        try
        {
            var repository = await CreateRepositoryAsync(harness.RootPath, "serial-repository");
            var firstId = (await harness.RunAsync(
                Path.Combine(repository, "one"),
                ["add", "b7-serial-one"])).StandardOutput.Trim();
            var secondId = (await harness.RunAsync(
                Path.Combine(repository, "two"),
                ["add", "b7-serial-two"])).StandardOutput.Trim();

            await harness.StartWorkerAsync(concurrency: 2);
            await harness.WaitForFileAsync(harness.TaskPath("done", firstId));
            await harness.WaitForFileAsync(harness.TaskPath("done", secondId));
            await harness.StopWorkerAsync();

            var first = await ReadIntervalAsync(harness, "b7-serial-one");
            var second = await ReadIntervalAsync(harness, "b7-serial-two");

            Assert.True(
                first.EndedAtUtc <= second.StartedAtUtc
                || second.EndedAtUtc <= first.StartedAtUtc,
                $"Expected serialized intervals, but observed " +
                $"[{first.StartedAtUtc:O}, {first.EndedAtUtc:O}] and " +
                $"[{second.StartedAtUtc:O}, {second.EndedAtUtc:O}].");
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    [Fact]
    public async Task OriginalCheckoutAndLinkedWorktree_DoOverlap()
    {
        var harness = new VeloCliHarness();
        var repository = string.Empty;
        var linkedWorktree = string.Empty;
        var worktreeCreated = false;
        try
        {
            repository = await CreateRepositoryAsync(harness.RootPath, "parallel-repository");
            linkedWorktree = Path.Combine(harness.RootPath, "linked-worktree");
            await RunGitAsync(
                repository,
                "worktree", "add", "--detach", linkedWorktree, "HEAD");
            worktreeCreated = true;

            var mainId = (await harness.RunAsync(
                Path.Combine(repository, "one"),
                ["add", "b7-parallel-main"])).StandardOutput.Trim();
            var linkedId = (await harness.RunAsync(
                Path.Combine(linkedWorktree, "two"),
                ["add", "b7-parallel-linked"])).StandardOutput.Trim();

            await harness.StartWorkerAsync(concurrency: 2);
            await harness.WaitForFileAsync(harness.TaskPath("done", mainId));
            await harness.WaitForFileAsync(harness.TaskPath("done", linkedId));
            await harness.StopWorkerAsync();

            var main = await ReadIntervalAsync(harness, "b7-parallel-main");
            var linked = await ReadIntervalAsync(harness, "b7-parallel-linked");

            Assert.True(
                main.StartedAtUtc < linked.EndedAtUtc
                && linked.StartedAtUtc < main.EndedAtUtc,
                $"Expected overlapping intervals, but observed " +
                $"[{main.StartedAtUtc:O}, {main.EndedAtUtc:O}] and " +
                $"[{linked.StartedAtUtc:O}, {linked.EndedAtUtc:O}].");
        }
        finally
        {
            try { await harness.StopWorkerAsync(); }
            catch { }

            if (worktreeCreated && Directory.Exists(repository))
            {
                try
                {
                    await RunGitAsync(
                        repository,
                        "worktree", "remove", "--force", linkedWorktree);
                    await RunGitAsync(repository, "worktree", "prune");
                }
                catch
                {
                }
            }

            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    private static async Task<string> CreateRepositoryAsync(string rootPath, string name)
    {
        var repository = Path.Combine(rootPath, name);
        Directory.CreateDirectory(Path.Combine(repository, "one"));
        Directory.CreateDirectory(Path.Combine(repository, "two"));
        await File.WriteAllTextAsync(Path.Combine(repository, "one", "tracked.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(repository, "two", "tracked.txt"), "two");

        await RunGitAsync(repository, "init");
        await RunGitAsync(repository, "add", ".");
        await RunGitAsync(
            repository,
            "-c", "user.name=Velo Tests",
            "-c", "user.email=velo-tests@example.invalid",
            "commit", "-m", "initial");
        return repository;
    }

    private static async Task<ProbeInterval> ReadIntervalAsync(
        VeloCliHarness harness,
        string prompt)
    {
        var path = harness.HomePath($"{prompt}.interval.json");
        await harness.WaitForFileAsync(path);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return new ProbeInterval(
            document.RootElement.GetProperty("startedAtUtc").GetDateTimeOffset(),
            document.RootElement.GetProperty("endedAtUtc").GetDateTimeOffset());
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
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
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"git {string.Join(' ', arguments)} did not exit within 20 seconds.");
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} exited with {process.ExitCode}." +
                $"{Environment.NewLine}stdout: {output}" +
                $"{Environment.NewLine}stderr: {error}");
        }
    }

    private sealed record ProbeInterval(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset EndedAtUtc);
}
