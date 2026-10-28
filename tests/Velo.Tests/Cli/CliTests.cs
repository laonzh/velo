using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Velo.Tests;

public class CliTests(ServerFixture s) : IClassFixture<ServerFixture>
{
    private static int NumberFrom(string text) => int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"VL-(\d+)").Groups[1].Value);

    [Fact]
    public async Task AddLsShowRunLogsRm()
    {
        (int code, string created, _) = await s.Cli("add", "cli job", "-m", "the description");
        Assert.Equal(0, code);
        Assert.Matches(@"^created VL-\d+ \(\w+\)", created);
        int n = NumberFrom(created);
        await s.WaitReview(n);

        (int lsCode, string ls, _) = await s.Cli("ls");
        Assert.Equal(0, lsCode);
        Assert.Contains("KEY", ls);
        Assert.Matches($@"VL-{n}\s+review/completed\s+1\s+.*\s-\s+cli job", ls); // updated + dir ("-": no directory) before the title

        (_, string json, _) = await s.Cli("--json"); // no command = ls
        Assert.Contains(JsonDocument.Parse(json).RootElement.EnumerateArray(), i => i.GetProperty("number").GetInt32() == n);

        (int showCode, string show, _) = await s.Cli("show", n.ToString());
        Assert.Equal(0, showCode);
        Assert.Contains($"VL-{n}  cli job", show);
        Assert.Contains("the description", show);
        Assert.Contains("#1 completed", show);
        Assert.Contains("take over: cd", show);
        Assert.Contains("codex resume", show);

        (int runCode, string run, _) = await s.Cli("run", $"vl-{n}", "-m", "more");
        Assert.Equal(0, runCode);
        Assert.Matches($@"^VL-{n} ", run);
        await s.WaitReview(n, runs: 2);

        (int logsCode, string logs, _) = await s.Cli("logs", $"VL-{n}", "--run", "2");
        Assert.Equal(0, logsCode);
        Assert.Contains("run 2 in", logs);
        (int followCode, string followed, _) = await s.Cli("logs", n.ToString(), "-f");
        Assert.Equal(0, followCode);
        Assert.Contains("agent finished: completed", followed);

        (int noDiff, _, string noDiffErr) = await s.Cli("diff", n.ToString());
        Assert.Equal(1, noDiff);
        Assert.Contains("has no directory", noDiffErr);

        (int noYes, _, string noYesErr) = await s.Cli("rm", n.ToString()); // stdin is closed: the prompt is declined
        Assert.Equal(1, noYes);
        Assert.Contains("not deleted", noYesErr);
        Assert.Equal(HttpStatusCode.OK, (await s.Send(HttpMethod.Get, $"/api/issues/{n}")).Status);

        (int rmCode, string rm, _) = await s.Cli("rm", n.ToString(), "-y");
        Assert.Equal(0, rmCode);
        Assert.Equal($"deleted VL-{n}", rm.Trim());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Send(HttpMethod.Get, $"/api/issues/{n}")).Status);
    }

    [Fact]
    public async Task Wait_BlocksUntilTheRunEnds_ExitCodeTellsSuccessFromFailure()
    {
        (_, string created, _) = await s.Cli("add", "wait for me");
        int n = NumberFrom(created);
        (int code, string waited, _) = await s.Cli("wait", n.ToString()); // returns once the run is over, not before
        Assert.Equal(0, code);
        Assert.Equal($"VL-{n} review/completed", waited.Trim());
        (int again, _, _) = await s.Cli("wait", $"vl-{n}"); // already in review: returns at once
        Assert.Equal(0, again);

        s.Fake.Scenario = "failed_turn";
        try
        {
            (_, string failedCreated, _) = await s.Cli("add", "will fail");
            int f = NumberFrom(failedCreated);
            (int failCode, string failed, _) = await s.Cli("wait", f.ToString());
            Assert.Equal(1, failCode);
            Assert.Equal($"VL-{f} review/failed", failed.Trim());
        }
        finally
        {
            s.Fake.Scenario = "complete";
        }
        Assert.Equal(2, (await s.Cli("wait")).Code); // missing id
    }

    [Fact]
    public async Task GitIssue_AddWithDirectory_DiffAndApprove()
    {
        s.Fake.Scenario = "write_file";
        try
        {
            (int code, string created, _) = await s.Cli("add", "git via cli", "-d", s.Repo);
            Assert.Equal(0, code);
            int n = NumberFrom(created);
            await s.WaitReview(n);

            (_, string diff, _) = await s.Cli("diff", $"VL-{n}");
            Assert.Contains("+++ b/written-1.txt", diff);

            (_, string shown, _) = await s.Cli("show", n.ToString());
            Assert.Contains("merge:     clean into main", shown);

            (int approveCode, string approved, _) = await s.Cli("approve", n.ToString());
            Assert.Equal(0, approveCode);
            Assert.Equal($"VL-{n} done, merged into main", approved.Trim());
            Assert.True(File.Exists(Path.Combine(s.Repo, "written-1.txt")));
            Assert.Equal(0, (await s.Cli("rm", n.ToString(), "-y")).Code);
        }
        finally
        {
            s.Fake.Scenario = "complete";
        }
    }

    [Fact]
    public async Task Add_ReadsTheDescriptionFromStdin_WhenGivenDash()
    {
        (int code, string created, _) = await s.CliWithInput("line one\nline two\n", "add", "from stdin", "-m", "-");
        Assert.Equal(0, code);
        int n = NumberFrom(created);
        await s.WaitReview(n);
        (_, string json, _) = await s.Cli("show", n.ToString(), "--json");
        Assert.Equal("line one\nline two", JsonDocument.Parse(json).RootElement.GetProperty("description").GetString());
        Assert.Equal(0, (await s.Cli("rm", n.ToString(), "-y")).Code);
    }

    [Fact]
    public async Task Ls_ListsWhatNeedsAttentionFirst_WithDirectoryAndAge()
    {
        using var temp = new TempDir();
        string repo = TestGit.InitRepo(temp.Combine("my-project"));
        s.Fake.Scenario = "complete";
        (_, string a, _) = await s.Cli("add", "reviewed one", "-d", repo);
        int n = NumberFrom(a);
        await s.WaitReview(n);
        (_, string ls, _) = await s.Cli("ls");
        string line = ls.Split('\n').Single(l => l.StartsWith($"VL-{n} "));
        Assert.Matches(@"review/completed\s+1\s+(just now|\d+m ago)\s+my-project\s+reviewed one", line);
        Assert.Matches(@"^KEY\s+STATUS\s+RUNS\s+UPDATED\s+DIR\s+TITLE", ls);
        Assert.Equal(0, (await s.Cli("rm", n.ToString(), "-y")).Code);
    }

    [Theory]
    [InlineData("bash", "complete -F _velo velo")]
    [InlineData("powershell", "Register-ArgumentCompleter -Native -CommandName velo")]
    public async Task Completion_PrintsAScriptThatKnowsTheCommands(string shell, string marker)
    {
        (int code, string script, _) = await s.Cli("completion", shell);
        Assert.Equal(0, code);
        Assert.Contains(marker, script);
        Assert.Contains("approve", script);
        Assert.Contains("--effort", script);
        (int bad, _, string err) = await s.Cli("completion", "fish");
        Assert.Equal(2, bad);
        Assert.Contains("unknown shell", err);
    }

    [Fact]
    public async Task ApproveConflict_IsExitCode3_AndShowWarnsBeforehand()
    {
        using var temp = new TempDir();
        string repo = TestGit.InitRepo(temp.Combine("repo")); // its own repository: written-1.txt must not exist yet
        s.Fake.Scenario = "write_file";
        try
        {
            (_, string created, _) = await s.Cli("add", "will conflict", "-d", repo);
            int n = NumberFrom(created);
            await s.WaitReview(n);
            TestGit.CommitFile(repo, "written-1.txt", "the other side\n", "main edits the same file");

            (_, string shown, _) = await s.Cli("show", n.ToString());
            Assert.Contains("merge:     conflicts with main in written-1.txt", shown);

            (int code, string stdout, string err) = await s.Cli("approve", n.ToString());
            Assert.Equal(3, code);
            Assert.Equal("", stdout);
            Assert.Contains("conflicts with main in written-1.txt", err);
            Assert.Contains("git merge main", err);
            Assert.Equal(0, (await s.Cli("rm", n.ToString(), "-y")).Code);
        }
        finally
        {
            s.Fake.Scenario = "complete";
        }
    }

    [Fact]
    public async Task ServerErrors_AreExitCode1WithTheMessage()
    {
        (int code, _, string err) = await s.Cli("show", "99999");
        Assert.Equal(1, code);
        Assert.Contains("not found", err);

        using var plain = new TempDir();
        (int addCode, _, string addErr) = await s.Cli("add", "x", "-d", plain.Path);
        Assert.Equal(1, addCode);
        Assert.Contains("not a git repository", addErr);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("frobnicate")]
    [InlineData("show")]
    [InlineData("show", "abc")]
    [InlineData("ls", "extra")]
    [InlineData("add")]
    [InlineData("add", "t", "--run", "1")]
    [InlineData("add", "t", "-m")]
    [InlineData("logs", "1", "--run", "zero")]
    [InlineData("serve", "now")]
    public async Task UsageErrors_AreExitCode2(params string[] args)
    {
        (int code, string stdout, string err) = await s.Cli(args);
        Assert.Equal(2, code);
        Assert.StartsWith("velo: ", err);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task HelpAndVersion()
    {
        (int help, string helpText, _) = await s.Cli("--help");
        Assert.Equal(0, help);
        Assert.Contains("usage: velo", helpText);
        (int version, string versionText, _) = await s.Cli("-v");
        Assert.Equal(0, version);
        Assert.Matches(@"^velo \d+\.\d+\.\d+", versionText);
    }

    [Fact]
    public async Task ServerNotRunning_IsExitCode1WithTheServeHint()
    {
        using var temp = new TempDir();
        string home = temp.Combine("home");
        ServerFixture.WriteConfig(home, ServerFixture.FreePort());

        (int code, string stdout, string err) = await ServerFixture.Cli(home, "ls");

        Assert.Equal(1, code);
        Assert.Equal("", stdout);
        Assert.Contains("velo serve", err);
    }

    [Fact]
    public async Task InvalidConfig_IsExitCode1()
    {
        using var temp = new TempDir();
        string home = temp.Combine("home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.json"), """{"port": 0}""");

        (int code, _, string err) = await ServerFixture.Cli(home, "ls");

        Assert.Equal(1, code);
        Assert.Contains("port", err);
    }
}
