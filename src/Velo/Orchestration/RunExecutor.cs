using System.Globalization;

namespace Velo;

/// <summary>
/// One run of an issue: prepares the workspace, runs the configured agent, commits the result
/// and moves the issue to review. Never throws.
/// </summary>
internal sealed class RunExecutor(VeloPaths paths, Store store, Settings settings, AgentRegistry agents, Func<bool> shuttingDown)
{
    /// <summary>Executes the issue's last run (already recorded as running by the scheduler).</summary>
    public async Task ExecuteAsync(Issue issue, CancellationToken ct)
    {
        int number = issue.Number;
        try
        {
            Run run = issue.Runs[^1];
            string logFile = paths.RunLog(number, run.Number), cwd = paths.Workspace(number), tmp = paths.Tmp(number);
            var logGate = new Lock();
            void Log(string line)
            {
                string text = $"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {line.TrimEnd()}\n";
                lock (logGate)
                {
                    try { File.AppendAllText(logFile, text); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a log problem must not break the run
                }
            }

            AgentResult result;
            try
            {
                Directory.CreateDirectory(paths.Logs(number));
                Log($"velo: {issue.Key} run {run.Number} in {cwd}");
                string? baseBranch = null;
                if (issue.Directory is { } repo)
                {
                    await Git.EnsureWorktreeAsync(repo, cwd, Git.Branch(store.HomeId, number));
                    baseBranch = await Git.BaseBranchAsync(repo, Git.Branch(store.HomeId, number));
                }
                else Directory.CreateDirectory(cwd);
                Directory.CreateDirectory(tmp);
                var request = new AgentRequest
                {
                    Directory = cwd,
                    Prompt = PromptBuilder.Build(issue, cwd, baseBranch),
                    Model = issue.Model ?? settings.Model,
                    Effort = issue.Effort ?? settings.Effort,
                    WritableRoots = [tmp],
                    Environment = new Dictionary<string, string> { ["TEMP"] = tmp, ["TMP"] = tmp, ["TMPDIR"] = tmp },
                };
                // resolved per run: an unknown agent fails this run like an agent that cannot start
                IAgent agent = agents.Get(settings.Agent);
                result = await agent.RunAsync(request, Log, ct);
            }
            catch (Exception ex)
            {
                result = new AgentResult(ct.IsCancellationRequested ? AgentStatus.Cancelled : AgentStatus.Failed, null, null, 0, ex.Message);
            }

            bool shutdown = shuttingDown();
            (RunStatus status, string? summary) = result.Status switch
            {
                AgentStatus.Completed => (RunStatus.Completed, result.FinalMessage),
                AgentStatus.Failed => (RunStatus.Failed, result.Error),
                _ when shutdown => (RunStatus.Interrupted, "server stopped"),
                _ => (RunStatus.Stopped, result.FinalMessage ?? "stopped by user"),
            };
            if (status == RunStatus.Failed && !string.IsNullOrWhiteSpace(summary)) Log($"velo: error: {summary}");
            Log($"velo: agent finished: {Engine.Name(status)}");

            if (issue.Directory is not null && Directory.Exists(cwd))
            {
                try
                {
                    await Git.ValidateWorktreeAsync(issue.Directory, cwd, Git.Branch(store.HomeId, number));
                    string body = summary is null ? "" : summary.Length > 2000 ? summary[..2000] : summary;
                    if (await Git.CommitAllAsync(cwd, $"{issue.Key}: {issue.Title}\n\n{body}")) Log("velo: committed the changes");
                    else Log("velo: no changes to commit");
                }
                catch (Exception ex)
                {
                    status = RunStatus.Failed;
                    summary = (summary + "\n\ncommit failed: " + ex.Message).Trim();
                    Log("velo: commit failed: " + ex.Message);
                }
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            store.Update(number, i => i with
            {
                Status = IssueStatus.Review,
                UpdatedAt = now,
                Runs = [.. i.Runs.Select(r => r.Number != run.Number ? r : r with
                {
                    Status = status, Summary = summary, Tokens = result.Tokens, SessionId = result.SessionId, EndedAt = now,
                })],
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"velo: run of VL-{number} failed: {ex}");
        }
    }
}
