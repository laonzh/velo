namespace Velo.Abstractions;

public sealed record TaskItem(
    string Id,
    string Title,
    string Description,
    string Source,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record AgentRunContext(
    TaskItem TaskItem,
    string WorkspacePath,
    ILogWriter Logger);

public sealed record AgentRunResult(
    bool Success,
    int ExitCode,
    TimeSpan Elapsed,
    string? ErrorMessage);

public sealed record ProcessResult(bool Success, string Output, string Error);