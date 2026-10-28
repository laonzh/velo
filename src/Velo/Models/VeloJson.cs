using System.Text.Json;
using System.Text.Json.Serialization;

namespace Velo;

/// <summary>Enums as lowercase strings ("todo", "completed").</summary>
public sealed class LowerCaseEnum<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase) where T : struct, Enum;

/// <summary>
/// <c>Default</c> is the API flavour; <see cref="Files"/> is the indented on-disk flavour without computed properties.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    Converters = [typeof(LowerCaseEnum<IssueStatus>), typeof(LowerCaseEnum<RunStatus>)])]
[JsonSerializable(typeof(Issue))]
[JsonSerializable(typeof(IReadOnlyList<Issue>))]
[JsonSerializable(typeof(State))]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(AgentOptions))]
[JsonSerializable(typeof(NewIssue))]
[JsonSerializable(typeof(RunRequest))]
[JsonSerializable(typeof(LogChunk))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(IReadOnlyList<AgentModel>))]
[JsonSerializable(typeof(MergeCheck))]
public sealed partial class VeloJson : JsonSerializerContext
{
    private static VeloJson? s_files;

    public static VeloJson Files => s_files ??= new VeloJson(new JsonSerializerOptions(Default.Options)
    {
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
    });
}
