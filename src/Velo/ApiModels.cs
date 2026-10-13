using System.Text.Json;
using System.Text.Json.Serialization;

namespace Velo;

// Wire shapes for the HTTP API. Field names follow Symphony's snake_case convention.
internal sealed record ApiError(string Code, string Message);

internal sealed record ApiErrorEnvelope(ApiError Error);

internal sealed record WorkspaceInfo(string Path);

internal sealed record WorkCounts(int Pending, int Running, int Succeeded, int Failed);

internal sealed record WorkSummary(
    string WorkId,
    string Title,
    string Status,
    WorkspaceInfo Workspace,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    bool CancelRequested,
    string? LastError,
    bool Reviewable,
    bool Discarded,
    bool Accepted);

internal sealed record StateResponse(
    DateTimeOffset GeneratedAt,
    WorkCounts Counts,
    WorkSummary[] Running,
    WorkSummary[] Pending);

internal sealed record WorkListResponse(WorkSummary[] Work);

internal sealed record LogInfo(string Path, bool Exists);

internal sealed record WorkDetail(
    string WorkId,
    string Status,
    string Prompt,
    WorkspaceInfo Workspace,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int? ExitCode,
    bool CancelRequested,
    string? LastError,
    LogInfo Logs,
    bool Reviewable,
    bool Discarded,
    bool Accepted);

internal sealed record LogResponse(string WorkId, long Offset, long NextOffset, string Text);

internal sealed record WorkStatusResponse(string WorkId, string Status);

internal sealed record CancelResponse(string WorkId, string Result);

internal sealed record DiffResponse(string WorkId, string Base, string Stat, string Patch, bool Truncated);

internal sealed record DiscardResponse(string WorkId, bool Discarded);

internal sealed record AcceptResponse(string WorkId, bool Accepted);

internal sealed record RefreshResponse(
    bool Queued,
    bool Coalesced,
    DateTimeOffset RequestedAt,
    string[] Operations);

// Request bodies are classes so that missing fields stay null and can be reported.
internal sealed class AddWorkRequest
{
    public string? Prompt { get; set; }
    public string? Workspace { get; set; }
    public bool Worktree { get; set; }
}

internal sealed class EditWorkRequest
{
    public string? Prompt { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiErrorEnvelope))]
[JsonSerializable(typeof(StateResponse))]
[JsonSerializable(typeof(WorkListResponse))]
[JsonSerializable(typeof(WorkDetail))]
[JsonSerializable(typeof(LogResponse))]
[JsonSerializable(typeof(WorkStatusResponse))]
[JsonSerializable(typeof(CancelResponse))]
[JsonSerializable(typeof(DiffResponse))]
[JsonSerializable(typeof(DiscardResponse))]
[JsonSerializable(typeof(AcceptResponse))]
[JsonSerializable(typeof(RefreshResponse))]
[JsonSerializable(typeof(AddWorkRequest))]
[JsonSerializable(typeof(EditWorkRequest))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
