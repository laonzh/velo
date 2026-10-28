namespace Velo;

/// <summary>A new issue; only <see cref="Title"/> is required (a blank title is rejected).</summary>
public sealed record NewIssue(string? Title, string? Description = null, string? Directory = null, string? Model = null, string? Effort = null);

/// <summary>
/// Model and Effort are both left alone when both are null; otherwise they replace the issue's choice
/// (blank = agent default).
/// </summary>
public sealed record RunRequest(string? Message, string? Model = null, string? Effort = null);

/// <summary>A piece of a run log; <see cref="Offset"/> is the byte position of the next read.</summary>
public sealed record LogChunk(int Run, long Offset, string Text, bool Done);

/// <summary>The body of every error response: <c>{ "error": "..." }</c>.</summary>
public sealed record ErrorBody(string Error);

/// <summary>
/// Would approving merge cleanly? <see cref="Ahead"/> = commits only on the issue branch,
/// <see cref="Behind"/> = commits only on <see cref="Target"/> (the repository's current branch);
/// <see cref="Conflicts"/> = paths that would conflict, null when git cannot tell (merge-tree needs git 2.38).
/// </summary>
public sealed record MergeCheck(string Target, int Ahead, int Behind, IReadOnlyList<string>? Conflicts);

/// <summary>
/// A model the agent offers. <see cref="Efforts"/> are the reasoning efforts it supports;
/// <see cref="Effort"/> is its default.
/// </summary>
public sealed record AgentModel(string Id, string Name, bool Default, string? Effort, IReadOnlyList<string> Efforts);
