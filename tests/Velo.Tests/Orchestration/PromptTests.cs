using System.Text.Json;

namespace Velo.Tests;

public class PromptTests
{
    private static Issue Make(string? directory, string description, params Run[] runs) => new()
    {
        Number = 3, Title = "Fix it", Description = description, Directory = directory, Runs = runs,
    };

    private static Run Run(int n, string? summary = null, string? message = null, RunStatus status = RunStatus.Completed) =>
        new() { Number = n, Summary = summary, Message = message, Status = status };

    [Fact]
    public void FirstRun_HasTitleDescriptionAndRulesOnly()
    {
        string p = PromptBuilder.Build(Make(null, "Do the thing.", Run(1)), @"C:\w\vl-3");

        Assert.StartsWith("You are working on task VL-3: Fix it\n\nDo the thing.\n\nRules:", p);
        Assert.Contains(@"Work only inside C:\w\vl-3.", p);
        Assert.DoesNotContain("Previous run summary", p);
        Assert.DoesNotContain("Follow-up from the user", p);
    }

    [Fact]
    public void FollowUp_WithPreviousSummary()
    {
        string p = PromptBuilder.Build(Make(null, "", Run(1, "S1"), Run(2, "S2", "please add tests")), "/w");

        Assert.Contains("Previous run summary:\nS1\n\n", p);
        Assert.DoesNotContain("S2", p); // only the run before the current one is summarised
        Assert.Contains("Follow-up from the user:\nplease add tests\n\n", p);
        Assert.True(p.IndexOf("Previous run summary", StringComparison.Ordinal) < p.IndexOf("Follow-up from the user", StringComparison.Ordinal));
        Assert.StartsWith("You are working on task VL-3: Fix it\n\nPrevious run summary", p); // no empty description block
    }

    [Fact]
    public void FollowUpOnly_AndMissingPreviousSummary()
    {
        string first = PromptBuilder.Build(Make(null, "", Run(1, message: "hint")), "/w");
        Assert.Contains("Follow-up from the user:\nhint", first);
        Assert.DoesNotContain("Previous run summary", first);

        string noSummary = PromptBuilder.Build(Make(null, "", Run(1, summary: null), Run(2)), "/w");
        Assert.DoesNotContain("Previous run summary", noSummary);
    }

    [Fact]
    public void EarlierFollowUps_AreRepeatedOldestFirst_BeforeThePreviousSummaryAndTheNewFollowUp()
    {
        string p = PromptBuilder.Build(Make(null, "", Run(1, "S1", "use the standard library"), Run(2, "S2", "no new\ndependencies"), Run(3, message: "add tests")), "/w");

        Assert.Contains("Earlier follow-ups from the user (oldest first; they still apply):\n- run 1: use the standard library\n- run 2: no new\n  dependencies\n\n", p);
        Assert.Contains("Previous run summary:\nS2\n\n", p);
        Assert.Contains("Follow-up from the user:\nadd tests\n\n", p);
        Assert.True(p.IndexOf("Earlier follow-ups", StringComparison.Ordinal) < p.IndexOf("Previous run summary", StringComparison.Ordinal));
        Assert.True(p.IndexOf("Previous run summary", StringComparison.Ordinal) < p.IndexOf("Follow-up from the user", StringComparison.Ordinal));

        string noNewMessage = PromptBuilder.Build(Make(null, "", Run(1, "S1", "keep it short"), Run(2)), "/w");
        Assert.Contains("- run 1: keep it short\n", noNewMessage); // still applies to a plain run again
        Assert.DoesNotContain("Follow-up from the user:", noNewMessage);
    }

    [Theory]
    [InlineData(RunStatus.Completed, "Previous run summary:\nS1")]
    [InlineData(RunStatus.Failed, "Previous run failed:\nS1")]
    [InlineData(RunStatus.Stopped, "Previous run was stopped by the user:\nS1")]
    [InlineData(RunStatus.Interrupted, "Previous run was interrupted:\nS1")]
    public void PreviousRun_IsLabelledByHowItEnded(RunStatus status, string expected)
    {
        string p = PromptBuilder.Build(Make(null, "", Run(1, "S1", status: status), Run(2)), "/w");
        Assert.Contains(expected + "\n\n", p);
    }

    [Fact]
    public void LaterGitRuns_PointAtTheCommittedChangesOfEarlierRuns()
    {
        string withBase = PromptBuilder.Build(Make(@"C:\repo", "", Run(1, "S1"), Run(2)), "/w", "main");
        Assert.Contains("- The changes of earlier runs are already committed on this branch (see `git log main..HEAD` and `git diff main...HEAD`).\n", withBase);

        string noBase = PromptBuilder.Build(Make(@"C:\repo", "", Run(1, "S1"), Run(2)), "/w");
        Assert.Contains("- The changes of earlier runs are already committed on this branch (see `git log`).\n", noBase);

        Assert.DoesNotContain("earlier runs", PromptBuilder.Build(Make(@"C:\repo", "", Run(1)), "/w", "main")); // first run
        Assert.DoesNotContain("earlier runs", PromptBuilder.Build(Make(null, "", Run(1, "S1"), Run(2)), "/w")); // scratch issue
    }

    [Fact]
    public void GitIssue_ForbidsCommitting_ScratchIssue_DoesNot()
    {
        string git = PromptBuilder.Build(Make(@"C:\repo", "", Run(1)), "/w");
        Assert.Contains(@"This is a git worktree of C:\repo. Do not commit, branch, merge or rebase; Velo commits your changes when you finish.", git);
        Assert.DoesNotContain("scratch directory", git);

        string scratch = PromptBuilder.Build(Make(null, "", Run(1)), "/w");
        Assert.Contains("This is a scratch directory; leave any result files here.", scratch);
        Assert.DoesNotContain("git worktree", scratch);
        Assert.EndsWith("or the questions you need answered.\n", scratch);
    }
}
