using System.Text;

namespace Velo;

/// <summary>The fixed prompt of a run; project instructions come from AGENTS.md.</summary>
public static class PromptBuilder
{
    /// <summary>
    /// The prompt for the issue's last run, working in <paramref name="cwd"/>. Every run starts a new agent
    /// session, so all follow-ups so far are repeated; <paramref name="baseBranch"/> (when known) is the
    /// branch a git issue started from.
    /// </summary>
    public static string Build(Issue issue, string cwd, string? baseBranch = null)
    {
        var sb = new StringBuilder($"You are working on task {issue.Key}: {issue.Title}\n\n");
        if (issue.Description.Length > 0) sb.Append(issue.Description).Append("\n\n");

        Run[] earlier = [.. issue.Runs.SkipLast(1).Where(r => r.Message is { Length: > 0 })];
        if (earlier.Length > 0)
        {
            sb.Append("Earlier follow-ups from the user (oldest first; they still apply):\n");
            foreach (Run r in earlier) sb.Append($"- run {r.Number}: {r.Message!.Replace("\n", "\n  ")}\n");
            sb.Append('\n');
        }
        if (issue.Runs.Count > 1 && issue.Runs[^2] is { Summary: { Length: > 0 } summary } previous)
        {
            string label = previous.Status switch
            {
                RunStatus.Failed => "Previous run failed",
                RunStatus.Stopped => "Previous run was stopped by the user",
                RunStatus.Interrupted => "Previous run was interrupted",
                _ => "Previous run summary",
            };
            sb.Append($"{label}:\n{summary}\n\n");
        }
        if (issue.Runs.Count > 0 && issue.Runs[^1].Message is { Length: > 0 } message) sb.Append($"Follow-up from the user:\n{message}\n\n");

        sb.Append($"Rules:\n- Work only inside {cwd}.\n");
        sb.Append(issue.Directory is null
            ? "- This is a scratch directory; leave any result files here.\n"
            : $"- This is a git worktree of {issue.Directory}. Do not commit, branch, merge or rebase; Velo commits your changes when you finish.\n");
        if (issue.Directory is not null && issue.Runs.Count > 1)
            sb.Append(baseBranch is null
                ? "- The changes of earlier runs are already committed on this branch (see `git log`).\n"
                : $"- The changes of earlier runs are already committed on this branch (see `git log {baseBranch}..HEAD` and `git diff {baseBranch}...HEAD`).\n");
        sb.Append("- When you are done, reply with a short summary of what you did, or the questions you need answered.\n");
        return sb.ToString();
    }
}
