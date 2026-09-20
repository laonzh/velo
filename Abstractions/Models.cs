namespace Velo.Abstractions;

public sealed record WorkItem(
    string ProjectId,
    string Id,
    string Title,
    string Description,
    string Source,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record AgentRunContext(
    WorkItem WorkItem,
    string WorkspacePath,
    ILogWriter Logger);

public sealed record AgentRunResult(
    bool Success,
    int ExitCode,
    TimeSpan Elapsed,
    string? ErrorMessage);