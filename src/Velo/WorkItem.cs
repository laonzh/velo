using System.Text.Json.Serialization;

namespace Velo;

[JsonConverter(typeof(JsonStringEnumConverter<WorkState>))]
internal enum WorkState
{
    [JsonStringEnumMemberName("pending")]
    Pending,

    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    [JsonStringEnumMemberName("failed")]
    Failed
}

internal static class WorkStates
{
    public static bool TryParse(string value, out WorkState state) =>
        Enum.TryParse(value, true, out state) && Enum.IsDefined(state) && value == state.Text();

    public static string Text(this WorkState state) =>
        Enum.GetName(state)?.ToLowerInvariant()
        ?? throw new ArgumentOutOfRangeException(nameof(state), state, null);
}

internal sealed record WorkItem(
    [property: JsonRequired] string Id,
    [property: JsonRequired] WorkState State,
    [property: JsonRequired] string Prompt,
    [property: JsonRequired] string Workspace,
    [property: JsonRequired] DateTimeOffset CreatedAt,
    string? Error = null,
    int Attempts = 0,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    int? ExitCode = null,
    bool CancelRequested = false,
    // Set only for work that runs in its own worktree: the repository it was created from and
    // the commit that worktree started at.
    string? Repository = null,
    string? BaseCommit = null,
    bool Discarded = false,
    // Set once the changes were applied to Repository. The worktree stays until it is discarded.
    bool Accepted = false)
{
    [JsonIgnore]
    public string Title => Prompt.Split('\n').First().Trim();

    // Only succeeded worktree work can be reviewed or discarded. A discarded worktree is gone.
    public bool CanReview() =>
        State == WorkState.Succeeded && Repository is not null && BaseCommit is not null && !Discarded;

    public string Summary()
    {
        var title = Title;
        return title.Length <= 60 ? title : title[..57] + "...";
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(WorkItem))]
internal sealed partial class VeloJsonContext : JsonSerializerContext;
