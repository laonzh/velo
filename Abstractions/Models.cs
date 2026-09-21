namespace Velo.Abstractions;

public sealed record TaskItem(
    string Id,
    string Title,
    string Description,
    string Source,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record AgentRunContext(
    string TaskId,
    string TaskTitle,
    string TaskDescription,
    string WorkspacePath,
    ILogWriter Logger);

public sealed record AgentRunResult(
    bool Success,
    TimeSpan Elapsed,
    string? ErrorMessage = null);

public sealed record ProcessResult(bool Success, string Output, string Error);