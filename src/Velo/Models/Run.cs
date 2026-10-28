using System.Text.Json.Serialization;

namespace Velo;

public enum RunStatus { Running, Completed, Failed, Stopped, Interrupted }

public sealed record Run
{
    public required int Number { get; init; }
    public RunStatus Status { get; init; }
    public string? Message { get; init; }
    public string? Summary { get; init; } // the agent's last message, or why the run failed
    public long Tokens { get; init; }
    public string? SessionId { get; init; } // for the agent's take-over command (IAgent.TakeOver)

    /// <summary>
    /// Legacy name of <see cref="SessionId"/> in older state.json files: read into SessionId, never written.
    /// </summary>
    [JsonPropertyName("threadId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyThreadId
    {
        get => null;
        init => SessionId ??= value;
    }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
}
