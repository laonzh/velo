using System.Text;

namespace Velo.Tests;

/// <summary>Issue life cycles with a real Engine, real git repositories and FakeCodex as the agent.</summary>
[Collection("agents")]
public class EngineFlowTests
{
    private static string Wt(Rig rig, int n) => rig.Paths.Workspace(n);

    [Fact]
    public async Task SeparateHomes_WithTheSameIssueNumber_IsolateBranchesDiffsAndCleanup()
    {
        await using Rig first = Rig.Create("write_file");
        await using Rig second = Rig.Create("write_file");
        string repo = first.NewRepo();
        TestGit.Run(repo, "branch", "velo/vl-1"); // never adopt or delete an old unscoped branch
        Assert.Equal(1, (await first.Add("first home", repo)).Number);
        Assert.Equal(RunStatus.Completed, (await first.InReview("1")).Runs[^1].Status);
        Assert.Equal(1, (await second.Add("second home", repo)).Number);
        Assert.Equal(RunStatus.Completed, (await second.InReview("1")).Runs[^1].Status);

        TestGit.CommitFile(Wt(first, 1), "only-first.txt", "first", "first home change");
        TestGit.CommitFile(Wt(second, 1), "only-second.txt", "second", "second home change");
        string firstDiff = await first.Engine.DiffAsync("1"), secondDiff = await second.Engine.DiffAsync("1");
        Assert.Contains("only-first.txt", firstDiff);
        Assert.DoesNotContain("only-second.txt", firstDiff);
        Assert.Contains("only-second.txt", secondDiff);
        Assert.DoesNotContain("only-first.txt", secondDiff);
        Assert.Equal(2, (await second.Engine.MergeCheckAsync("1")).Ahead);

        await first.Engine.DeleteAsync("1");
        Assert.False(TestGit.BranchExists(repo, Git.Branch(first.Store.HomeId, 1)));
        Assert.True(TestGit.BranchExists(repo, Git.Branch(second.Store.HomeId, 1)));
        Assert.True(File.Exists(Path.Combine(Wt(second, 1), "only-second.txt")));
        second.Engine.RunIssue("1", "again");
        Assert.Equal(RunStatus.Completed, (await second.InReview("1", runs: 2)).Runs[^1].Status);
        Assert.Equal(IssueStatus.Done, (await second.Engine.ApproveAsync("1")).Status);
        Assert.True(File.Exists(Path.Combine(repo, "only-second.txt")));
        Assert.False(File.Exists(Path.Combine(repo, "only-first.txt")));
        Assert.False(TestGit.BranchExists(repo, Git.Branch(second.Store.HomeId, 1)));
        Assert.True(TestGit.BranchExists(repo, "velo/vl-1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_RejectsForeignWorkspace_PreservesFilesAndIssue_AndAllowsRetry(bool foreignRepository)
    {
        await using Rig rig = Rig.Create();
        string repo = rig.NewRepo();
        string workspace = Wt(rig, 1);
        if (foreignRepository)
        {
            TestGit.InitRepo(workspace);
            TestGit.Run(workspace, "checkout", "-b", Git.Branch(rig.Store.HomeId, 1));
        }
        else Directory.CreateDirectory(workspace);
        string privateFile = Path.Combine(workspace, "private.txt");
        File.WriteAllText(privateFile, "preserve human work");
        await rig.Add("invalid workspace", repo);
        await rig.InReview("1");

        await Assert.ThrowsAsync<GitException>(() => rig.Engine.DeleteAsync("1"));

        Assert.Equal("preserve human work", File.ReadAllText(privateFile));
        Assert.Equal(IssueStatus.Review, rig.Engine.Get("1").Status);
        Assert.NotNull(new Store(rig.Paths).Get(1));
        Assert.True(Directory.Exists(rig.Paths.Logs(1)));
        string preserved = Path.Combine(rig.Paths.Root, "preserved-workspace");
        Directory.Move(workspace, preserved);
        await rig.Engine.DeleteAsync("1");
        Assert.Equal(404, Assert.Throws<VeloException>(() => rig.Engine.Get("1")).Status);
        Assert.Null(new Store(rig.Paths).Get(1));
        Assert.Equal("preserve human work", File.ReadAllText(Path.Combine(preserved, "private.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnfinishedWorkspaceMerge_RejectsApproveAndRunCommit_WithoutChangingTheIndex(bool stagedResolution)
    {
        await using Rig rig = Rig.Create("write_file");
        string repo = rig.NewRepo();
        TestGit.CommitFile(repo, "shared.txt", "base\n", "base");
        await rig.Add("conflicting", repo);
        await rig.InReview("1");
        string workspace = Wt(rig, 1);
        TestGit.CommitFile(workspace, "shared.txt", "task\n", "task edit");
        TestGit.CommitFile(repo, "shared.txt", "main\n", "main edit");
        Assert.NotEqual(0, TestGit.Try(workspace, "merge", "main").Code);
        if (stagedResolution)
        {
            File.WriteAllText(Path.Combine(workspace, "shared.txt"), "human resolution\n");
            TestGit.Run(workspace, "add", "shared.txt");
        }
        string head = TestGit.Run(workspace, "rev-parse", "HEAD");
        string mergeHead = TestGit.Run(workspace, "rev-parse", "MERGE_HEAD");
        string index = TestGit.Run(workspace, "ls-files", "--stage");
        string content = File.ReadAllText(Path.Combine(workspace, "shared.txt"));

        await Assert.ThrowsAsync<GitException>(() => rig.Engine.ApproveAsync("1"));
        Assert.Equal(IssueStatus.Review, rig.Engine.Get("1").Status);
        rig.Engine.RunIssue("1", "retry");
        Assert.Equal(RunStatus.Failed, (await rig.InReview("1", runs: 2)).Runs[^1].Status);

        Assert.Equal(head, TestGit.Run(workspace, "rev-parse", "HEAD"));
        Assert.Equal(mergeHead, TestGit.Run(workspace, "rev-parse", "MERGE_HEAD"));
        Assert.Equal(index, TestGit.Run(workspace, "ls-files", "--stage"));
        Assert.Equal(content, File.ReadAllText(Path.Combine(workspace, "shared.txt")));
        Assert.Equal("main\n", File.ReadAllText(Path.Combine(repo, "shared.txt")));
        Assert.True(Directory.Exists(workspace));
    }

    [Fact]
    public async Task WorkspaceThroughDirectoryLink_RunsAgainAndApproves()
    {
        await using Rig rig = Rig.Create("write_file");
        string repo = rig.NewRepo();
        string target = Path.Combine(rig.Paths.Root, "physical-workspaces");
        string link = Path.GetDirectoryName(Wt(rig, 1))!;
        Directory.CreateDirectory(target);
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, target);
        try
        {
            await rig.Add("linked workspace", repo);
            Assert.Equal(RunStatus.Completed, (await rig.InReview("1")).Runs[^1].Status);
            rig.Engine.RunIssue("1", "again");
            Assert.Equal(RunStatus.Completed, (await rig.InReview("1", runs: 2)).Runs[^1].Status);
            Assert.Equal(IssueStatus.Done, (await rig.Engine.ApproveAsync("1")).Status);
            Assert.True(File.Exists(Path.Combine(repo, "written-1.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "written-2.txt")));
            Assert.False(Directory.Exists(Wt(rig, 1)));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task ExistingPlainWorkspace_DoesNotCommitTheEnclosingRepository()
    {
        await using Rig rig = Rig.Create(seed: paths =>
        {
            TestGit.InitRepo(paths.Root);
            Directory.CreateDirectory(paths.Workspace(1));
            File.WriteAllText(Path.Combine(paths.Root, "private.txt"), "unrelated changes");
        });
        string head = TestGit.Run(rig.Paths.Root, "rev-parse", "HEAD");
        await rig.Add("invalid workspace", rig.Paths.Root);
        Issue review = await rig.InReview("1");

        Assert.Equal(RunStatus.Failed, review.Runs[^1].Status);
        Assert.Empty(rig.Fake.Prompts());
        Assert.Equal(head, TestGit.Run(rig.Paths.Root, "rev-parse", "HEAD"));
        Assert.Equal("", TestGit.Run(rig.Paths.Root, "diff", "--cached"));
        await Assert.ThrowsAsync<GitException>(() => rig.Engine.ApproveAsync("1"));
        Assert.True(Directory.Exists(Wt(rig, 1)));
        Assert.Equal(head, TestGit.Run(rig.Paths.Root, "rev-parse", "HEAD"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedWorkspaceBranch_RejectsApproveAndRun_WithoutCommitting(bool detached)
    {
        await using Rig rig = Rig.Create("write_file");
        string repo = rig.NewRepo();
        await rig.Add("takeover", repo);
        await rig.InReview("1");
        string workspace = Wt(rig, 1);
        TestGit.Run(workspace, detached ? ["checkout", "--detach"] : ["checkout", "-b", "human-work"]);
        File.WriteAllText(Path.Combine(workspace, "manual.txt"), "preserve this");
        TestGit.Run(workspace, "add", "manual.txt");
        string head = TestGit.Run(workspace, "rev-parse", "HEAD");
        string staged = TestGit.Run(workspace, "diff", "--cached");

        await Assert.ThrowsAsync<GitException>(() => rig.Engine.ApproveAsync("1"));
        Assert.Equal(IssueStatus.Review, rig.Engine.Get("1").Status);
        Assert.True(Directory.Exists(workspace));
        Assert.False(File.Exists(Path.Combine(repo, "written-1.txt")));
        rig.Engine.RunIssue("1", "retry");
        Issue failed = await rig.InReview("1", runs: 2);
        Assert.Equal(RunStatus.Failed, failed.Runs[^1].Status);
        Assert.Single(rig.Fake.Prompts());
        Assert.Equal(head, TestGit.Run(workspace, "rev-parse", "HEAD"));
        Assert.Equal(staged, TestGit.Run(workspace, "diff", "--cached"));
        Assert.Equal("preserve this", File.ReadAllText(Path.Combine(workspace, "manual.txt")));
    }

    [Fact]
    public async Task WorkspaceInAnotherRepository_IsRejectedEvenOnTheExpectedBranch()
    {
        await using Rig rig = Rig.Create();
        string repo = rig.NewRepo();
        string workspace = TestGit.InitRepo(Wt(rig, 1));
        TestGit.Run(workspace, "checkout", "-b", Git.Branch(rig.Store.HomeId, 1));
        string head = TestGit.Run(workspace, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(workspace, "manual.txt"), "preserve this");
        await rig.Add("wrong repository", repo);
        Issue review = await rig.InReview("1");

        Assert.Equal(RunStatus.Failed, review.Runs[^1].Status);
        Assert.Empty(rig.Fake.Prompts());
        await Assert.ThrowsAsync<GitException>(() => rig.Engine.ApproveAsync("1"));
        Assert.Equal(head, TestGit.Run(workspace, "rev-parse", "HEAD"));
        Assert.True(File.Exists(Path.Combine(workspace, "manual.txt")));
    }

    [Fact]
    public async Task BranchChangedDuringRun_DoesNotCommitOnTheOtherBranch()
    {
        await using Rig rig = Rig.Create("write_stall");
        string repo = rig.NewRepo();
        await rig.Add("branch changed during execution", repo);
        string workspace = Wt(rig, 1);
        await Wait.Until(() => File.Exists(Path.Combine(workspace, "written-1.txt")), "partial changes");
        TestGit.Run(workspace, "checkout", "-b", "human-work");
        string head = TestGit.Run(workspace, "rev-parse", "HEAD");

        Issue stopped = await rig.Engine.StopAsync("1");

        Assert.Equal(RunStatus.Failed, stopped.Runs[^1].Status);
        Assert.Contains($"expected {Git.Branch(rig.Store.HomeId, 1)}", stopped.Runs[^1].Summary);
        Assert.Equal(head, TestGit.Run(workspace, "rev-parse", "HEAD"));
        Assert.Equal("", TestGit.Run(workspace, "diff", "--cached"));
        Assert.True(File.Exists(Path.Combine(workspace, "written-1.txt")));
    }

    [Fact]
    public async Task WithTakeOver_AddsTheAgentCommand_OnlyForTheApiView()
    {
        await using Rig rig = Rig.Create("write_file");
        await rig.Add("scratch job");
        Issue review = await rig.InReview("1");

        string session = review.Runs[^1].SessionId!;
        Assert.Null(review.TakeOver); // never stored
        Assert.Equal($"codex resume {session}", rig.Engine.WithTakeOver(review).TakeOver);
        Assert.Null(rig.Engine.WithTakeOver(review with { Runs = [] }).TakeOver);
        Assert.DoesNotContain("takeOver", File.ReadAllText(rig.Paths.StateFile));
    }

    [Fact]
    public async Task ScratchIssue_RunsReviewsAndIsApproved_WorkspaceKept()
    {
        await using Rig rig = Rig.Create("write_file");
        Issue added = await rig.Add("scratch job");
        Assert.Equal("VL-1", added.Key);
        Assert.Null(added.Directory);

        Issue review = await rig.InReview("1");

        Run run = Assert.Single(review.Runs);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal("wrote written-1.txt", run.Summary);
        Assert.False(string.IsNullOrEmpty(run.SessionId));
        Assert.NotNull(run.EndedAt);
        Assert.True(File.Exists(Path.Combine(Wt(rig, 1), "written-1.txt")));
        Assert.True(Directory.Exists(rig.Paths.Tmp(1)));
        Assert.Equal(404, (await Assert.ThrowsAsync<VeloException>(() => rig.Engine.DiffAsync("1"))).Status);

        Issue done = await rig.Engine.ApproveAsync("vl-1");

        Assert.Equal(IssueStatus.Done, done.Status);
        Assert.True(File.Exists(Path.Combine(Wt(rig, 1), "written-1.txt"))); // result files stay until rm
        Assert.Empty(rig.Engine.List());
        Assert.Single(rig.Engine.List(all: true));

        await rig.Engine.DeleteAsync("1");
        Assert.False(Directory.Exists(Wt(rig, 1)));
    }

    [Fact]
    public async Task GitIssue_CommitsOnItsBranch_ApproveMergesNoFastForwardAndCleansUp()
    {
        await using Rig rig = Rig.Create("write_file");
        string repo = rig.NewRepo();
        string mainHead = TestGit.Run(repo, "rev-parse", "HEAD");

        Issue added = await rig.Add("add a file", repo);
        Assert.Equal(Path.GetFullPath(repo), added.Directory, ignoreCase: true);
        Issue review = await rig.InReview("1");

        Assert.Equal(RunStatus.Completed, review.Runs[0].Status);
        Assert.Equal("VL-1: add a file", TestGit.Run(repo, "log", "-1", "--format=%s", Git.Branch(rig.Store.HomeId, 1)));
        Assert.Contains("wrote written-1.txt", TestGit.Run(repo, "log", "-1", "--format=%b", Git.Branch(rig.Store.HomeId, 1)));
        Assert.Equal(mainHead, TestGit.Run(repo, "rev-parse", "HEAD"));
        Assert.Equal("", TestGit.Run(Wt(rig, 1), "status", "--porcelain")); // Velo committed everything
        string diff = await rig.Engine.DiffAsync("1");
        Assert.Contains("+++ b/written-1.txt", diff);
        Assert.Contains("+change from FakeCodex turn 1", diff);

        Issue done = await rig.Engine.ApproveAsync("1");

        Assert.Equal(IssueStatus.Done, done.Status);
        Assert.Equal("Merge VL-1: add a file", TestGit.Run(repo, "log", "-1", "--format=%s"));
        TestGit.Run(repo, "rev-parse", "--verify", "HEAD^2"); // a real merge commit
        Assert.Equal("change from FakeCodex turn 1", File.ReadAllText(Path.Combine(repo, "written-1.txt")).Trim());
        Assert.False(Directory.Exists(Wt(rig, 1)));
        Assert.False(TestGit.BranchExists(repo, Git.Branch(rig.Store.HomeId, 1)));
        Assert.Single(TestGit.Run(repo, "worktree", "list").Split('\n'));
        Assert.Equal("", await rig.Engine.DiffAsync("1"));
    }

    [Fact]
    public async Task FollowUpRun_GetsPreviousSummaryAndMessage_AndCommitsAgain()
    {
        await using Rig rig = Rig.Create("write_file");
        string repo = rig.NewRepo();
        await rig.Add("two rounds", repo);
        await rig.InReview("1");

        Issue queued = rig.Engine.RunIssue("VL-1", "please add tests");
        Assert.True(queued.Status is IssueStatus.Todo or IssueStatus.Running);
        Issue review = await rig.InReview("1", runs: 2);

        Assert.Equal([null, "please add tests"], review.Runs.Select(r => r.Message));
        Assert.Null(review.Message);
        List<string> prompts = rig.Fake.Prompts();
        Assert.Equal(2, prompts.Count);
        Assert.DoesNotContain("Previous run summary", prompts[0]);
        Assert.DoesNotContain("Follow-up from the user", prompts[0]);
        Assert.Contains("Previous run summary:\nwrote written-1.txt\n", prompts[1]);
        Assert.Contains("Follow-up from the user:\nplease add tests\n", prompts[1]);
        Assert.Contains("see `git log main..HEAD` and `git diff main...HEAD`", prompts[1]);
        Assert.Equal("2", TestGit.Run(repo, "rev-list", "--count", $"main..{Git.Branch(rig.Store.HomeId, 1)}"));
        Assert.True(File.Exists(Path.Combine(Wt(rig, 1), "written-2.txt")));
        Assert.Contains("run-2.log", string.Join(',', Directory.GetFiles(rig.Paths.Logs(1)).Select(Path.GetFileName)));

        rig.Engine.RunIssue("1", "and update the docs");
        await rig.InReview("1", runs: 3);
        string third = rig.Fake.Prompts()[2];
        Assert.Contains("Earlier follow-ups from the user (oldest first; they still apply):\n- run 2: please add tests\n", third);
        Assert.Contains("Follow-up from the user:\nand update the docs\n", third);
    }

    [Fact]
    public async Task RunAgain_CanSwitchModelAndEffort_AndLeavesThemAloneOtherwise()
    {
        await using Rig rig = Rig.Create("complete");
        await rig.Add("switch", rig.NewRepo());
        await rig.InReview("1");
        Assert.Equal((null, null), (rig.Engine.Get("1").Model, rig.Engine.Get("1").Effort));

        rig.Engine.RunIssue("VL-1", null, "gpt-x", "high");
        Issue second = await rig.InReview("1", runs: 2);
        Assert.Equal(("gpt-x", "high"), (second.Model, second.Effort));

        rig.Engine.RunIssue("VL-1", "again");
        Issue third = await rig.InReview("1", runs: 3);
        Assert.Equal(("gpt-x", "high"), (third.Model, third.Effort));

        rig.Engine.RunIssue("VL-1", null, "", "");
        Issue fourth = await rig.InReview("1", runs: 4);
        Assert.Equal((null, null), (fourth.Model, fourth.Effort));
    }

    [Theory]
    [InlineData("failed_turn", "upstream overloaded")]
    [InlineData("crash", "")]
    public async Task AgentFailure_FailsTheRun_AndHandsTheIssueToReview(string scenario, string expectedText)
    {
        await using Rig rig = Rig.Create(scenario);
        string repo = rig.NewRepo();
        await rig.Add("will fail", repo);

        Issue review = await rig.InReview("1");

        Run run = review.Runs[0];
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.False(string.IsNullOrWhiteSpace(run.Summary));
        Assert.Contains(expectedText, run.Summary);
        Assert.Equal("0", TestGit.Run(repo, "rev-list", "--count", $"main..{Git.Branch(rig.Store.HomeId, 1)}")); // nothing to commit
        string log = File.ReadAllText(rig.Paths.RunLog(1, 1));
        Assert.Contains("velo: error: " + run.Summary!.Split('\n')[0].TrimEnd(), log); // the reason is in the log, not only in the run summary
        Assert.Contains("agent finished: failed", log);
    }

    [Fact]
    public async Task StopRunning_KillsAgent_CommitsPartialChanges_ThenReview()
    {
        await using Rig rig = Rig.Create("write_stall");
        string repo = rig.NewRepo();
        await rig.Add("long job", repo);
        await rig.Running("1");
        await Wait.Until(() => File.Exists(Path.Combine(Wt(rig, 1), "written-1.txt")), "the agent's partial change");

        Issue stopped = await rig.Engine.StopAsync("1");

        Assert.Equal(IssueStatus.Review, stopped.Status);
        Assert.Equal(RunStatus.Stopped, stopped.Runs[0].Status);
        Assert.NotNull(stopped.Runs[0].EndedAt);
        Assert.Contains("written-1.txt", TestGit.Run(repo, "show", "--stat", "--format=%s", Git.Branch(rig.Store.HomeId, 1)));
        Assert.Equal("VL-1: long job", TestGit.Run(repo, "log", "-1", "--format=%s", Git.Branch(rig.Store.HomeId, 1)));
    }

    [Fact]
    public async Task Delete_StopsARunningAgent_AndRemovesEverything()
    {
        await using Rig rig = Rig.Create("write_stall");
        string repo = rig.NewRepo();
        await rig.Add("doomed", repo);
        await rig.Running("1");
        await Wait.Until(() => File.Exists(Path.Combine(Wt(rig, 1), "written-1.txt")), "the agent's partial change");
        Assert.True(Directory.Exists(rig.Paths.Tmp(1)));
        Assert.True(Directory.Exists(rig.Paths.Logs(1)));

        await rig.Engine.DeleteAsync("VL-1");

        Assert.False(Directory.Exists(Wt(rig, 1)));
        Assert.False(Directory.Exists(rig.Paths.Tmp(1)));
        Assert.False(Directory.Exists(rig.Paths.Logs(1)));
        Assert.False(TestGit.BranchExists(repo, Git.Branch(rig.Store.HomeId, 1)));
        Assert.Single(TestGit.Run(repo, "worktree", "list").Split('\n'));
        Assert.Equal(404, Assert.Throws<VeloException>(() => rig.Engine.Get("1")).Status);
        Assert.Empty(rig.Store.List());
    }

    [Fact]
    public async Task ApproveConflict_Is409_KeepsReview_LeavesNoMergeState_ThenManualMergeAllowsApprove()
    {
        await using Rig rig = Rig.Create("write_file");
        string repo = rig.NewRepo();
        TestGit.CommitFile(repo, "shared.txt", "base\n", "add shared");
        await rig.Add("conflicting", repo);
        await rig.InReview("1");
        File.WriteAllText(Path.Combine(Wt(rig, 1), "shared.txt"), "branch\n"); // a manual edit, committed by approve
        TestGit.CommitFile(repo, "shared.txt", "main\n", "main edit");

        VeloException ex = await Assert.ThrowsAsync<VeloException>(() => rig.Engine.ApproveAsync("1"));

        Assert.Equal(409, ex.Status);
        Assert.Contains("VL-1 conflicts with main in shared.txt", ex.Message); // names the files and says what to do
        Assert.Contains("git merge main", ex.Message);
        Assert.Equal(IssueStatus.Review, rig.Engine.Get("1").Status);
        Assert.NotEqual(0, TestGit.Try(repo, "rev-parse", "-q", "--verify", "MERGE_HEAD").Code);
        Assert.Equal("", TestGit.Run(repo, "status", "--porcelain"));
        Assert.Equal("VL-1: manual changes", TestGit.Run(repo, "log", "-1", "--format=%s", Git.Branch(rig.Store.HomeId, 1)));

        Assert.NotEqual(0, TestGit.Try(repo, "merge", Git.Branch(rig.Store.HomeId, 1)).Code); // the human resolves it by hand
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "resolved\n");
        TestGit.Run(repo, "add", "-A");
        TestGit.Run(repo, "commit", "-q", "-m", "resolve");

        Issue done = await rig.Engine.ApproveAsync("1");

        Assert.Equal(IssueStatus.Done, done.Status);
        Assert.False(TestGit.BranchExists(repo, Git.Branch(rig.Store.HomeId, 1)));
        Assert.False(Directory.Exists(Wt(rig, 1)));
        Assert.Equal("resolved", File.ReadAllText(Path.Combine(repo, "shared.txt")).Trim());
    }

    [Fact]
    public async Task Approve_RefusesAnotherCheckedOutBranch_AndMergesOnlyIntoTheBaseBranch()
    {
        await using Rig rig = Rig.Create("write_file");
        string repo = rig.NewRepo();
        await rig.Add("based", repo);
        await rig.InReview("1");
        Assert.Equal("main", TestGit.Run(repo, "config", "--get", $"branch.{Git.Branch(rig.Store.HomeId, 1)}.veloBase"));
        TestGit.Run(repo, "checkout", "-q", "-b", "elsewhere");

        VeloException ex = await Assert.ThrowsAsync<VeloException>(() => rig.Engine.ApproveAsync("1"));

        Assert.Equal(409, ex.Status);
        Assert.Contains("VL-1 was branched from main, but", ex.Message);
        Assert.Contains("has elsewhere checked out", ex.Message);
        Assert.Equal(IssueStatus.Review, rig.Engine.Get("1").Status);
        Assert.NotEqual(0, TestGit.Try(repo, "merge-base", "--is-ancestor", Git.Branch(rig.Store.HomeId, 1), "elsewhere").Code);

        TestGit.Run(repo, "checkout", "-q", "main");
        Assert.Equal(IssueStatus.Done, (await rig.Engine.ApproveAsync("1")).Status);
        Assert.True(File.Exists(Path.Combine(repo, "written-1.txt")));
        Assert.Equal("", TestGit.Run(repo, "ls-tree", "--name-only", "elsewhere", "written-1.txt"));
        Assert.NotEqual(0, TestGit.Try(repo, "config", "--get", $"branch.{Git.Branch(rig.Store.HomeId, 1)}.veloBase").Code); // removed with the branch
    }

    [Fact]
    public async Task MergeCheck_ReportsAheadBehindAndConflicts_WithoutTouchingTheRepository()
    {
        await using Rig rig = Rig.Create("write_file");
        string repo = rig.NewRepo();
        TestGit.CommitFile(repo, "shared.txt", "base\n", "base");
        await rig.Add("checked", repo);
        await rig.InReview("1");

        MergeCheck clean = await rig.Engine.MergeCheckAsync("1");
        Assert.Equal(("main", 1, 0), (clean.Target, clean.Ahead, clean.Behind));
        Assert.Empty(clean.Conflicts!);

        TestGit.CommitFile(repo, "other.txt", "x\n", "main moves on");
        MergeCheck behind = await rig.Engine.MergeCheckAsync("1");
        Assert.Equal((1, 1), (behind.Ahead, behind.Behind));
        Assert.Empty(behind.Conflicts!);

        TestGit.CommitFile(Wt(rig, 1), "shared.txt", "branch\n", "branch edit");
        TestGit.CommitFile(repo, "shared.txt", "main\n", "main edit");
        MergeCheck conflicting = await rig.Engine.MergeCheckAsync("1");
        Assert.Equal(["shared.txt"], conflicting.Conflicts);
        Assert.Equal("", TestGit.Run(repo, "status", "--porcelain"));
        Assert.NotEqual(0, TestGit.Try(repo, "rev-parse", "-q", "--verify", "MERGE_HEAD").Code);

        await using Rig scratch = Rig.Create();
        await scratch.Add("no directory");
        Assert.Equal(404, (await Assert.ThrowsAsync<VeloException>(() => scratch.Engine.MergeCheckAsync("1"))).Status);
    }

    [Fact]
    public async Task ApproveDuringManualMerge_PreservesTheMergeAndStagedResolution()
    {
        await using Rig rig = Rig.Create("write_file");
        string repo = rig.NewRepo();
        TestGit.CommitFile(repo, "shared.txt", "base\n", "base");
        await rig.Add("manual merge", repo);
        await rig.InReview("1");
        TestGit.CommitFile(Wt(rig, 1), "shared.txt", "branch\n", "branch edit");
        TestGit.CommitFile(repo, "shared.txt", "main\n", "main edit");
        Assert.NotEqual(0, TestGit.Try(repo, "merge", Git.Branch(rig.Store.HomeId, 1)).Code);
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "human resolved\n");
        TestGit.Run(repo, "add", "shared.txt");
        string mergeHead = TestGit.Run(repo, "rev-parse", "MERGE_HEAD");
        string staged = TestGit.Run(repo, "diff", "--cached");

        await Assert.ThrowsAsync<GitException>(() => rig.Engine.ApproveAsync("1"));

        Assert.Equal(mergeHead, TestGit.Run(repo, "rev-parse", "MERGE_HEAD"));
        Assert.Equal(staged, TestGit.Run(repo, "diff", "--cached"));
        Assert.Equal("human resolved\n", File.ReadAllText(Path.Combine(repo, "shared.txt")));
        Assert.Equal(IssueStatus.Review, rig.Engine.Get("1").Status);
        Assert.True(Directory.Exists(Wt(rig, 1)));

        TestGit.Run(repo, "commit", "-q", "-m", "finish human merge");
        Assert.Equal(IssueStatus.Done, (await rig.Engine.ApproveAsync("1")).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approve_RejectsConcurrentMutations_WhileMergingOrWaitingForTheMergeLock(bool waiting)
    {
        await using Rig rig = Rig.Create("write_file");
        string repo = rig.NewRepo();
        await rig.Add("first", repo);
        await rig.InReview("1");
        if (waiting)
        {
            await rig.Add("second", repo);
            await rig.InReview("2");
        }
        using var hook = new BlockingGitHook(repo, "pre-merge-commit", Path.Combine(rig.Paths.Root, "merge-hook"));
        Task<Issue> first = rig.Engine.ApproveAsync("1");
        Task<Issue>? second = null;
        string id = waiting ? "2" : "1";
        try
        {
            await hook.WaitUntilEntered();
            if (waiting) second = rig.Engine.ApproveAsync(id);

            Assert.Equal(409, Assert.Throws<VeloException>(() => rig.Engine.RunIssue(id, "another run")).Status);
            Assert.Equal(409, (await Assert.ThrowsAsync<VeloException>(() => rig.Engine.DeleteAsync(id))).Status);
            Assert.Equal(409, (await Assert.ThrowsAsync<VeloException>(() => rig.Engine.ApproveAsync(id))).Status);
            Assert.Single(rig.Engine.Get(id).Runs);
            Assert.True(File.Exists(Path.Combine(Wt(rig, int.Parse(id)), "written-1.txt")));

            Issue other = await rig.Add("unrelated scratch job");
            Assert.Equal(RunStatus.Completed, (await rig.InReview(other.Key)).Runs[^1].Status);
        }
        finally
        {
            hook.Dispose();
            await first;
            if (second is not null) await second;
        }

        Assert.Equal(IssueStatus.Done, rig.Engine.Get(id).Status);
        Assert.True(File.Exists(Path.Combine(repo, "written-1.txt")));
    }

    [Fact]
    public async Task Delete_RejectsConcurrentStop_WhileTheCancelledRunIsBeingCommitted()
    {
        await using Rig rig = Rig.Create("write_stall");
        string repo = rig.NewRepo();
        await rig.Add("delete during run", repo);
        await Wait.Until(() => File.Exists(Path.Combine(Wt(rig, 1), "written-1.txt")), "partial changes");
        using var hook = new BlockingGitHook(repo, "pre-commit", Path.Combine(rig.Paths.Root, "delete-hook"));
        Task deleting = rig.Engine.DeleteAsync("1");
        try
        {
            await hook.WaitUntilEntered();
            VeloException error = await Assert.ThrowsAsync<VeloException>(() => rig.Engine.StopAsync("1").WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(409, error.Status);
            Assert.Contains("operation in progress", error.Message);
        }
        finally
        {
            hook.Dispose();
            await deleting;
        }

        Assert.Equal(404, Assert.Throws<VeloException>(() => rig.Engine.Get("1")).Status);
        Assert.False(Directory.Exists(Wt(rig, 1)));
        Assert.False(TestGit.BranchExists(repo, Git.Branch(rig.Store.HomeId, 1)));
    }

    [Fact]
    public async Task LogDirectoryFailure_FailsTheRunAndAllowsAnotherRun()
    {
        await using Rig rig = Rig.Create(seed: paths =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Logs(1))!);
            File.WriteAllText(paths.Logs(1), "blocks the log directory");
        });
        await rig.Add("log setup fails");

        Issue failed = await rig.WaitFor("1", i => i.Status == IssueStatus.Review, "failed run in review", seconds: 3);

        Run run = Assert.Single(failed.Runs);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("vl-1", run.Summary);
        Assert.NotNull(run.EndedAt);
        Assert.Equal(RunStatus.Failed, new Store(rig.Paths).Get(1)!.Runs[0].Status);

        File.Delete(rig.Paths.Logs(1));
        rig.Engine.RunIssue("1", "try again");
        Issue retried = await rig.InReview("1", runs: 2);
        Assert.Equal(RunStatus.Completed, retried.Runs[^1].Status);
    }

    [Fact]
    public async Task UnwritableLogFile_DoesNotPreventRunCompletion()
    {
        await using Rig rig = Rig.Create(seed: paths => Directory.CreateDirectory(paths.RunLog(1, 1)));
        await rig.Add("logging is best effort");

        Issue review = await rig.WaitFor("1", i => i.Status == IssueStatus.Review, "completed run in review", seconds: 3);

        Assert.Equal(RunStatus.Completed, review.Runs[^1].Status);
        Assert.NotNull(review.Runs[^1].EndedAt);
        Assert.Equal(RunStatus.Completed, new Store(rig.Paths).Get(1)!.Runs[^1].Status);
    }

    [Fact]
    public async Task Add_Validation()
    {
        await using Rig rig = Rig.Create();
        using var plain = new TempDir();
        string repo = rig.NewRepo();
        Directory.CreateDirectory(Path.Combine(repo, "sub"));

        async Task<int> Status(NewIssue input) => (await Assert.ThrowsAsync<VeloException>(() => rig.Engine.AddAsync(input))).Status;
        Assert.Equal(400, await Status(new NewIssue("  ")));
        Assert.Equal(400, await Status(new NewIssue(null)));
        Assert.Equal(400, await Status(new NewIssue("t", Directory: plain.Path))); // not a git repository
        Assert.Equal(400, await Status(new NewIssue("t", Directory: Path.Combine(plain.Path, "missing"))));
        Assert.Equal(400, await Status(new NewIssue("t", Directory: Path.Combine("relative", "dir"))));
        string unborn = Path.Combine(plain.Path, "unborn");
        Directory.CreateDirectory(unborn);
        TestGit.Run(unborn, "init", "-q", "-b", "main");
        VeloException noCommits = await Assert.ThrowsAsync<VeloException>(() => rig.Engine.AddAsync(new NewIssue("t", Directory: unborn)));
        Assert.Equal(400, noCommits.Status);
        Assert.Contains("no commits", noCommits.Message);
        Assert.Empty(rig.Store.List());

        Issue sub = await rig.Engine.AddAsync(new NewIssue("  from sub  ", " desc ", Path.Combine(repo, "sub"), " m ", " "));
        Assert.Equal(Path.GetFullPath(repo), sub.Directory, ignoreCase: true); // normalised to the repository root
        Assert.Equal(("from sub", "desc", "m", null), (sub.Title, sub.Description, sub.Model, sub.Effort));
    }

    [Fact]
    public async Task InvalidTransitions_Are409_UnknownIs404_BadIdIs400()
    {
        await using Rig rig = Rig.Create("stall");
        await rig.Add("one");
        await rig.Running("1");

        Assert.Equal(409, Assert.Throws<VeloException>(() => rig.Engine.RunIssue("1", null)).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<VeloException>(() => rig.Engine.ApproveAsync("1"))).Status);
        await rig.Engine.StopAsync("1");
        await rig.Engine.ApproveAsync("1");
        Assert.Equal(409, (await Assert.ThrowsAsync<VeloException>(() => rig.Engine.ApproveAsync("1"))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<VeloException>(() => rig.Engine.StopAsync("1"))).Status);
        Assert.Equal(409, Assert.Throws<VeloException>(() => rig.Engine.RunIssue("1", null)).Status);
        Assert.Equal(404, Assert.Throws<VeloException>(() => rig.Engine.Get("7")).Status);
        Assert.Equal(400, Assert.Throws<VeloException>(() => rig.Engine.Get("seven")).Status);
    }

    [Fact]
    public async Task Log_ReadsFromOffsets_AndClamps()
    {
        await using Rig rig = Rig.Create();
        await using (Rig idle = Rig.Create(maxConcurrency: 0)) // nothing is started: an issue without runs
        {
            await idle.Add("never runs");
            Assert.Equal(new LogChunk(0, 0, "", true), idle.Engine.ReadLog("1", null, 0));
        }
        await rig.Add("logged");
        await rig.InReview("1");

        LogChunk all = rig.Engine.ReadLog("1", null, 0);
        Assert.Equal(1, all.Run);
        Assert.True(all.Done);
        Assert.Contains("velo: VL-1 run 1 in", all.Text);
        Assert.Contains("velo: agent finished: completed", all.Text);
        Assert.Equal(new FileInfo(rig.Paths.RunLog(1, 1)).Length, all.Offset);

        LogChunk none = rig.Engine.ReadLog("1", 1, all.Offset);
        Assert.Equal(("", all.Offset, true), (none.Text, none.Offset, none.Done));
        Assert.Equal(all.Offset, rig.Engine.ReadLog("1", null, 1_000_000).Offset); // beyond the end is clamped

        int firstLine = all.Text.IndexOf('\n') + 1;
        LogChunk rest = rig.Engine.ReadLog("1", null, Encoding.UTF8.GetByteCount(all.Text[..firstLine]));
        Assert.Equal(all.Text[firstLine..], rest.Text);
        Assert.Equal(404, Assert.Throws<VeloException>(() => rig.Engine.ReadLog("1", 5, 0)).Status);
    }
}

/// <summary>Scheduling, stopping and recovery.</summary>
[Collection("agents")]
public class EngineSchedulingTests
{
    [Fact]
    public async Task MaxConcurrency_IsRespected_AndTodoIssuesStartInFifoOrder()
    {
        await using Rig rig = Rig.Create("stall", maxConcurrency: 2);
        for (int i = 1; i <= 4; i++) await rig.Add("job " + i);

        Assert.Equal([IssueStatus.Running, IssueStatus.Running, IssueStatus.Todo, IssueStatus.Todo], rig.Engine.List().Select(i => i.Status));

        await rig.Engine.StopAsync("1"); // frees a slot: the lowest todo number goes next
        await rig.WaitFor("3", i => i.Status == IssueStatus.Running, "VL-3 running");

        Assert.Equal([IssueStatus.Review, IssueStatus.Running, IssueStatus.Running, IssueStatus.Todo], rig.Engine.List().Select(i => i.Status));
        Assert.Equal(RunStatus.Stopped, rig.Engine.Get("1").Runs[0].Status);

        rig.Fake.Scenario = "complete"; // later runs finish immediately
        await rig.Engine.StopAsync("2");
        await rig.WaitFor("4", i => i.Runs.Count == 1, "VL-4 started");
        await rig.Engine.StopAsync("3");
        await rig.InReview("4");
        Assert.All(rig.Engine.List(), i => Assert.Equal(IssueStatus.Review, i.Status));
    }

    [Fact]
    public async Task StateWriteFailure_WhenStartingAnIssue_LeavesItQueued_AndDoesNotFailTheAction()
    {
        await using Rig rig = Rig.Create("stall", maxConcurrency: 1);
        await rig.Add("running");
        await rig.Running("1");
        await rig.Add("queued");
        Directory.CreateDirectory(rig.Paths.StateFile + ".tmp"); // every state write fails

        await rig.Engine.StopAsync("1"); // frees the slot: starting VL-2 cannot be saved

        Assert.Equal(IssueStatus.Todo, rig.Engine.Get("2").Status);
        Directory.Delete(rig.Paths.StateFile + ".tmp");
        rig.Engine.RunIssue("2", "go"); // the next wake starts it
        await rig.Running("2");
    }

    [Fact]
    public async Task StopTodo_TakesItOutOfTheQueue_WithoutARun()
    {
        await using Rig rig = Rig.Create("stall", maxConcurrency: 1);
        await rig.Add("running");
        await rig.Running("1");
        Issue queued = await rig.Add("queued");
        Assert.Equal(IssueStatus.Todo, queued.Status);

        Issue stopped = await rig.Engine.StopAsync("2");

        Assert.Equal(IssueStatus.Review, stopped.Status);
        Assert.Empty(stopped.Runs);
        await rig.Engine.StopAsync("1"); // the slot frees up, but VL-2 is no longer queued
        Assert.Equal(IssueStatus.Review, rig.Engine.Get("2").Status);
        Assert.Empty(rig.Engine.Get("2").Runs);

        rig.Fake.Scenario = "complete";
        rig.Engine.RunIssue("2", null); // review → todo → runs
        await rig.InReview("2");
        Assert.Equal(RunStatus.Completed, rig.Engine.Get("2").Runs[0].Status);
    }

    [Fact]
    public async Task Restart_TurnsRunningIssuesIntoReviewWithAnInterruptedRun()
    {
        await using Rig rig = Rig.Create(seed: paths =>
        {
            var store = new Store(paths);
            DateTimeOffset t = DateTimeOffset.UtcNow;
            store.Add(n => new Issue
            {
                Number = n, Title = "was running", Status = IssueStatus.Running, CreatedAt = t, UpdatedAt = t,
                Runs = [new Run { Number = 1, Status = RunStatus.Completed, Summary = "first" }, new Run { Number = 2, Status = RunStatus.Running, StartedAt = t }],
            });
            store.Add(n => new Issue { Number = n, Title = "finished", Status = IssueStatus.Done, CreatedAt = t, UpdatedAt = t });
        });

        Issue recovered = rig.Engine.Get("1");

        Assert.Equal(IssueStatus.Review, recovered.Status);
        Assert.Equal(RunStatus.Completed, recovered.Runs[0].Status);
        Assert.Equal(RunStatus.Interrupted, recovered.Runs[1].Status);
        Assert.Equal("server stopped", recovered.Runs[1].Summary);
        Assert.NotNull(recovered.Runs[1].EndedAt);
        Assert.Equal(IssueStatus.Done, rig.Engine.Get("2").Status);
        Assert.Empty(rig.Fake.Prompts()); // nothing was started
    }

    [Fact]
    public async Task Shutdown_InterruptsTheRunningAgent_AndStartsNothingElse()
    {
        await using Rig rig = Rig.Create("stall", maxConcurrency: 1);
        await rig.Add("a");
        await rig.Add("b");
        await Wait.Until(() => rig.Fake.Prompts().Count == 1, "the agent working");

        await rig.Engine.ShutdownAsync();

        Issue a = rig.Engine.Get("1");
        Assert.Equal(IssueStatus.Review, a.Status);
        Assert.Equal((RunStatus.Interrupted, "server stopped"), (a.Runs[0].Status, a.Runs[0].Summary));
        Assert.Equal(IssueStatus.Todo, rig.Engine.Get("2").Status); // the freed slot is not refilled
        Assert.Equal(IssueStatus.Review, new Store(rig.Paths).Get(1)!.Status); // persisted
    }
}
