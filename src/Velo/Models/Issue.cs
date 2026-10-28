using System.Text.Json.Serialization;

namespace Velo;

public enum IssueStatus { Todo, Running, Review, Done }

public sealed record Issue
{
    public required int Number { get; init; }
    public string Key => "VL-" + Number; // API only: read-only properties are not written to state.json
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public string? Directory { get; init; } // git repository top level; null = scratch task without a directory
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public IssueStatus Status { get; init; }
    public string? Message { get; init; } // follow-up for the next run
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public IReadOnlyList<Run> Runs { get; init; } = [];

    /// <summary>
    /// API only, never stored: shell command to continue the last run by hand (<see cref="IAgent.TakeOver"/>),
    /// set by <see cref="Engine.WithTakeOver"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TakeOver { get; init; }
}
