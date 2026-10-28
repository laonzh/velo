using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Velo.Tests;

[Collection("agents")] // CodexAgent.IdleTimeout is a process-wide setting these tests change
public sealed class CodexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "velo-codex-tests", Guid.NewGuid().ToString("N"));
    private readonly TimeSpan _idle = CodexAgent.IdleTimeout;
    private readonly List<string> _log = [];

    public CodexTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        CodexAgent.IdleTimeout = _idle;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string FakeCodexPath
    {
        get
        {
            string? dir = typeof(CodexTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "FakeCodexDir")?.Value;
            Assert.False(string.IsNullOrEmpty(dir), "FakeCodexDir assembly metadata missing");
            string exe = Path.Combine(dir!, OperatingSystem.IsWindows() ? "FakeCodex.exe" : "FakeCodex");
            Assert.True(File.Exists(exe), $"FakeCodex not built at {exe}");
            return Path.GetFullPath(exe);
        }
    }

    private string LogFile => Path.Combine(_dir, "protocol.log");

    private (CodexAgent Agent, AgentRequest Request) Request(string scenario, string? codexPath = null)
    {
        var env = new Dictionary<string, string> { ["FAKE_CODEX_SCENARIO"] = scenario, ["FAKE_CODEX_LOG"] = LogFile };
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root) env["DOTNET_ROOT"] = root;
        return (new CodexAgent(codexPath ?? FakeCodexPath), new AgentRequest
        {
            Prompt = "do the thing",
            Directory = _dir,
            WritableRoots = [_dir],
            Effort = "low",
            Environment = env,
        });
    }

    private Task<AgentResult> Run((CodexAgent Agent, AgentRequest Request) run, CancellationToken ct = default)
    {
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return run.Agent.RunAsync(run.Request, line => { lock (_log) _log.Add(line); },
            CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token).Token);
    }

    private List<JsonElement> LoggedRequests(string method) =>
        File.ReadAllLines(LogFile).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.TryGetProperty("method", out var m) && m.GetString() == method).ToList();

    private static async Task AssertNoFakeCodexLeft()
    {
        for (int i = 0; i < 50; i++)
        {
            if (Process.GetProcessesByName("FakeCodex").Length == 0) return;
            await Task.Delay(100);
        }
        Assert.Empty(Process.GetProcessesByName("FakeCodex"));
    }

    [Fact]
    public async Task Complete_ReturnsMessageThreadAndTokens()
    {
        AgentResult r = await Run(Request("complete"));

        Assert.Equal(AgentStatus.Completed, r.Status);
        Assert.Null(r.Error);
        Assert.Contains("turn 1 done", r.FinalMessage);
        Assert.False(string.IsNullOrEmpty(r.SessionId));
        Assert.Equal(150, r.Tokens);
        lock (_log)
        {
            Assert.Contains(_log, l => l.StartsWith("agent: ") && l.Contains("turn 1 done"));
            Assert.Contains("tokens: 150", _log);
        }

        JsonElement thread = LoggedRequests("thread/start").Single().GetProperty("params");
        Assert.Equal("never", thread.GetProperty("approvalPolicy").GetString());
        Assert.Equal("workspace-write", thread.GetProperty("sandbox").GetString());
        JsonElement turn = LoggedRequests("turn/start").Single().GetProperty("params");
        Assert.False(turn.TryGetProperty("outputSchema", out _));
        Assert.Equal("workspaceWrite", turn.GetProperty("sandboxPolicy").GetProperty("type").GetString());
        Assert.False(turn.GetProperty("sandboxPolicy").GetProperty("networkAccess").GetBoolean());
        await AssertNoFakeCodexLeft();
    }

    [Fact]
    public async Task StderrFlood_DoesNotBlock()
    {
        AgentResult r = await Run(Request("stderr_flood"));
        Assert.Equal(AgentStatus.Completed, r.Status);
        Assert.Contains("survived stderr flood", r.FinalMessage);
    }

    [Fact]
    public async Task FailedTurn_IsFailedWithReason()
    {
        AgentResult r = await Run(Request("failed_turn"));
        Assert.Equal(AgentStatus.Failed, r.Status);
        Assert.Contains("upstream overloaded", r.Error);
        lock (_log) Assert.Contains(_log, l => l.StartsWith("error: ") && l.Contains("upstream overloaded"));
    }

    [Fact]
    public async Task Crash_IsFailedWithStderrTail()
    {
        AgentResult r = await Run(Request("crash"));
        Assert.Equal(AgentStatus.Failed, r.Status);
        Assert.Contains("simulated fatal error", r.Error);
        Assert.Contains("101", r.Error);
    }

    [Fact]
    public async Task Stall_HitsIdleTimeout()
    {
        CodexAgent.IdleTimeout = TimeSpan.FromSeconds(1.5);
        AgentResult r = await Run(Request("stall"));
        Assert.Equal(AgentStatus.Failed, r.Status);
        Assert.Contains("idle timeout", r.Error);
        await AssertNoFakeCodexLeft();
    }

    [Fact]
    public async Task Approval_IsDeclinedAndRunCompletes()
    {
        AgentResult r = await Run(Request("approval"));
        Assert.Equal(AgentStatus.Completed, r.Status);
        Assert.Contains("decision=decline;unknown=error", r.FinalMessage);
        lock (_log) Assert.Contains(_log, l => l.StartsWith("declined request item/commandExecution/requestApproval"));
    }

    [Fact]
    public async Task Cancellation_ReturnsCancelledAndKillsProcess()
    {
        using var cts = new CancellationTokenSource();
        Task<AgentResult> run = Run(Request("stall"), cts.Token);
        await Task.Delay(1500);
        Assert.False(run.IsCompleted);
        cts.Cancel();
        AgentResult r = await run;
        Assert.Equal(AgentStatus.Cancelled, r.Status);
        await AssertNoFakeCodexLeft();
    }

    [Fact]
    public async Task MissingExecutable_IsFailedNotThrown()
    {
        AgentResult r = await Run(Request("complete", Path.Combine(_dir, "no-such-codex.exe")));
        Assert.Equal(AgentStatus.Failed, r.Status);
        Assert.Contains("does not exist", r.Error);
    }

    [Fact]
    public void FindOnPath_HonoursPathExt_AndNpmShimResolvesToNativeBinary()
    {
        if (!OperatingSystem.IsWindows()) return;
        string shimDir = Path.Combine(_dir, "nodejs");
        string nativeDir = Path.Combine(shimDir, "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64",
            "vendor", "x86_64-pc-windows-msvc", "bin");
        Directory.CreateDirectory(nativeDir);
        File.WriteAllText(Path.Combine(shimDir, "codex"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(shimDir, "codex.cmd"), "@echo off\n");
        string native = Path.Combine(nativeDir, "codex.exe");
        File.WriteAllText(native, "");

        string? found = CodexLocator.FindOnPath("codex", Path.Combine(_dir, "empty") + Path.PathSeparator + shimDir, ".COM;.EXE;.BAT;.CMD");
        Assert.Equal(Path.Combine(shimDir, "codex.cmd"), found, ignoreCase: true);
        var (exe, npm) = CodexLocator.ResolveExecutable(found!);
        Assert.Equal(native, exe, ignoreCase: true);
        Assert.True(npm);

        File.Delete(native);
        Assert.Null(CodexLocator.ResolveExecutable(found!).Executable);
    }
}
