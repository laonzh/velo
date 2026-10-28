namespace Velo;

/// <summary>A coding agent that runs one turn for an issue. Implementations never throw for runtime problems.</summary>
public interface IAgent
{
    /// <summary>
    /// Runs the request to completion. Start failures, protocol errors, crashes and timeouts →
    /// <see cref="AgentStatus.Failed"/> (Error says why); <paramref name="ct"/> cancelled → the agent is killed
    /// and the result is <see cref="AgentStatus.Cancelled"/>.
    /// <paramref name="log"/> receives readable single lines (commands, file changes, agent messages, errors).
    /// </summary>
    Task<AgentResult> RunAsync(AgentRequest request, Action<string> log, CancellationToken ct);

    /// <summary>
    /// Models the agent offers (with the efforts each supports). Empty when unsupported or unavailable; never throws.
    /// </summary>
    Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct);

    /// <summary>Shell command to continue the session by hand, or null when unsupported.</summary>
    string? TakeOver(string sessionId);
}

/// <summary>One agent turn: what to run and where.</summary>
public sealed record AgentRequest
{
    public required string Prompt { get; init; }
    /// <summary>Absolute working directory of the agent (thread cwd).</summary>
    public required string Directory { get; init; }
    /// <summary>Extra writable roots of the workspace-write sandbox (the cwd itself is always writable).</summary>
    public IReadOnlyList<string> WritableRoots { get; init; } = [];
    public string? Model { get; init; }
    public string? Effort { get; init; }
    /// <summary>Variables overlaid on the inherited environment.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

public enum AgentStatus { Completed, Failed, Cancelled }

/// <summary>Outcome of a run. <see cref="Error"/> is set for Failed. Tokens = last reported session total.</summary>
public sealed record AgentResult(AgentStatus Status, string? FinalMessage, string? SessionId, long Tokens, string? Error);
