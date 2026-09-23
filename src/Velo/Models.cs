using System.Text.Json.Serialization;

namespace Velo;

public enum TaskState
{
    Todo,
    Running,
    Done,
    Failed,
    Cancelled
}

public sealed record TaskItem(
    string Title,
    string WorkspacePath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? LastError = null,
    bool Unsafe = false);

public sealed record TaskEntry(string Id, TaskState State, TaskItem Item);

public sealed record ProcessResult(int ExitCode, string? Error)
{
    public bool Success => ExitCode == 0;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(TaskItem))]
internal sealed partial class VeloJsonContext : JsonSerializerContext;
