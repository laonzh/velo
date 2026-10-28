using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Velo.Tests;

/// <summary>A temp directory removed on dispose (read-only git objects included).</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "velo-test", Guid.NewGuid().ToString("N")[..12]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (!Directory.Exists(Path)) return;
                foreach (string f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100); // a just-killed process may still hold a handle
            }
        }
    }
}

public static class Wait
{
    /// <summary>
    /// Polls until <paramref name="condition"/> is true; fails with <paramref name="what"/> after the timeout.
    /// </summary>
    public static async Task Until(Func<bool> condition, string what, int seconds = 30)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out after {seconds}s waiting for: {what}");
            await Task.Delay(25);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, string what, int seconds = 30)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out after {seconds}s waiting for: {what}");
            await Task.Delay(50);
        }
    }
}

/// <summary>The git command line, for preparing and inspecting repositories.</summary>
public static class TestGit
{
    public static string Run(string cwd, params string[] args)
    {
        (int code, string output, string error) = Try(cwd, args);
        Assert.True(code == 0, $"git {string.Join(' ', args)} failed: {error}");
        return output;
    }

    public static (int Code, string Output, string Error) Try(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        p.StandardInput.Close();
        Task<string> err = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout.Trim(), err.Result.Trim());
    }

    /// <summary>A repository on branch main with one commit (README.md).</summary>
    public static string InitRepo(string path)
    {
        Directory.CreateDirectory(path);
        Run(path, "init", "-q", "-b", "main");
        Run(path, "config", "user.name", "Test");
        Run(path, "config", "user.email", "test@example.invalid");
        Run(path, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(path, "README.md"), "hello\n");
        Run(path, "add", ".");
        Run(path, "commit", "-q", "-m", "initial");
        return path;
    }

    public static void CommitFile(string repo, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(repo, file), content);
        Run(repo, "add", "-A");
        Run(repo, "commit", "-q", "-m", message);
    }

    public static bool BranchExists(string repo, string branch) => Try(repo, "show-ref", "--verify", "--quiet", "refs/heads/" + branch).Code == 0;
}

/// <summary>Pauses git in a hook until released, with a timeout so failed tests cannot leave git hanging.</summary>
public sealed class BlockingGitHook : IDisposable
{
    private readonly string _entered, _release;

    public BlockingGitHook(string repo, string name, string signalDirectory)
    {
        Directory.CreateDirectory(signalDirectory);
        _entered = Path.Combine(signalDirectory, "entered");
        _release = Path.Combine(signalDirectory, "release");
        static string Quote(string path) => "'" + path.Replace('\\', '/').Replace("'", "'\"'\"'") + "'";
        string hook = Path.Combine(repo, ".git", "hooks", name);
        File.WriteAllText(hook, $"#!/bin/sh\ntouch {Quote(_entered)}\ni=0\nwhile [ ! -f {Quote(_release)} ]; do\n  i=$((i+1))\n  [ $i -le 200 ] || exit 1\n  sleep 0.1\ndone\n", new System.Text.UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public Task WaitUntilEntered() => Wait.Until(() => File.Exists(_entered), "git hook entered");
    public void Dispose() => File.WriteAllText(_release, "go");
}

/// <summary>
/// A private copy of the FakeCodex output directory, so every test has its own scenario (scenario.txt)
/// and protocol log (log.txt).
/// The executable is renamed so that it never matches the process name checked by CodexTests.
/// </summary>
public sealed class FakeCodexCopy
{
    private readonly string _dir;

    public FakeCodexCopy(string dir)
    {
        _dir = dir;
        string? source = typeof(FakeCodexCopy).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "FakeCodexDir")?.Value;
        Assert.False(string.IsNullOrEmpty(source), "FakeCodexDir assembly metadata missing");
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(source!).Where(f => !f.EndsWith(".pdb"))) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true);
        string exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "FakeCodex.exe" : "FakeCodex");
        Exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "fake-agent.exe" : "fake-agent");
        File.Move(exe, Exe);
        File.WriteAllText(Path.Combine(dir, "log.txt"), LogFile);
    }

    public string Exe { get; }
    public string LogFile => Path.Combine(_dir, "protocol.log");

    /// <summary>Takes effect for the next agent process.</summary>
    public string Scenario
    {
        set => File.WriteAllText(Path.Combine(_dir, "scenario.txt"), value);
    }

    /// <summary>The prompts of all turn/start requests so far, oldest first.</summary>
    public List<string> Prompts()
    {
        if (!File.Exists(LogFile)) return [];
        using var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        string text = new StreamReader(fs).ReadToEnd();
        var prompts = new List<string>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("method", out JsonElement m) && m.GetString() == "turn/start")
                prompts.Add(root.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString()!);
        }
        return prompts;
    }
}

/// <summary>A real <see cref="Engine"/> on a temp VELO_HOME with FakeCodex as the agent.</summary>
public sealed class Rig : IAsyncDisposable
{
    private readonly TempDir _temp = new();

    private Rig(string scenario, int maxConcurrency, Action<VeloPaths>? seed)
    {
        Paths = new VeloPaths(_temp.Combine("home"));
        Fake = new FakeCodexCopy(_temp.Combine("fake")) { Scenario = scenario };
        seed?.Invoke(Paths);
        Store = new Store(Paths);
        Engine = new Engine(Paths, Store, new Settings { CodexPath = Fake.Exe, MaxConcurrency = maxConcurrency });
        Engine.Start();
    }

    public static Rig Create(string scenario = "complete", int maxConcurrency = 2, Action<VeloPaths>? seed = null) => new(scenario, maxConcurrency, seed);

    public VeloPaths Paths { get; }
    public Store Store { get; }
    public Engine Engine { get; }
    public FakeCodexCopy Fake { get; }

    public string NewRepo(string name = "repo") => TestGit.InitRepo(_temp.Combine(name));

    public Task<Issue> Add(string title, string? directory = null) => Engine.AddAsync(new NewIssue(title, Directory: directory));

    public async Task<Issue> WaitFor(string id, Func<Issue, bool> predicate, string what, int seconds = 30)
    {
        try
        {
            await Wait.Until(() => predicate(Engine.Get(id)), what, seconds);
        }
        catch (TimeoutException ex)
        {
            Issue last = Engine.Get(id);
            throw new TimeoutException($"{ex.Message} ({last.Key} is {last.Status}, runs [{string.Join(", ", last.Runs.Select(r => $"{r.Status}:{r.Summary}"))}])");
        }
        return Engine.Get(id);
    }

    /// <summary>Waits until the issue is in review after exactly <paramref name="runs"/> runs.</summary>
    public Task<Issue> InReview(string id, int runs = 1) =>
        WaitFor(id, i => i.Status == IssueStatus.Review && i.Runs.Count == runs, $"{id} in review after {runs} run(s)");

    public Task<Issue> Running(string id) => WaitFor(id, i => i.Status == IssueStatus.Running, $"{id} running");

    public async ValueTask DisposeAsync()
    {
        await Engine.ShutdownAsync();
        _temp.Dispose();
    }
}
