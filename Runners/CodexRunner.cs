using System.Diagnostics;
using System.Text;
using Velo.Abstractions;
using Velo.Common;

namespace Velo.Runners;

public sealed class CodexRunner : IAgentRunner
{
    public string Name => "codex";
    private readonly IReadOnlyList<string> _args;

    public CodexRunner()
    {
        _args =
        [
            "exec",
            "--dangerously-bypass-approvals-and-sandbox",
            "--color", "never",
            "Please follow the task requirements described in TASK_PROMPT.md. Implement all required changes and verify your implementation."
        ];
    }

    public async Task<AgentRunResult> RunAsync(AgentRunContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 1. Prepare TASK_PROMPT.md inside the task's isolated workspace
        var promptPath = Path.Combine(context.WorkspacePath, "TASK_PROMPT.md");
        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine($"# Task: {context.TaskTitle}");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("## System Instructions");
        promptBuilder.AppendLine("You are an autonomous engineering agent operating in an isolated Git worktree.");
        promptBuilder.AppendLine("Complete the implementation as described below. Verify your changes with tests.");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("## Task Description");
        promptBuilder.AppendLine(context.TaskDescription);

        await File.WriteAllTextAsync(promptPath, promptBuilder.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        // 2. Spawn and monitor the Codex process
        var elapsed = Stopwatch.StartNew();
        var result = await Utils.ProcessRunAsync("codex", _args, context.WorkspacePath, context.Logger, cancellationToken).ConfigureAwait(false);
        elapsed.Stop();
        return new AgentRunResult(result.Success, elapsed.Elapsed, result.Error);
    }
}