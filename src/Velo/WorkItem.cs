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
    string Id,
    WorkState State,
    string Prompt,
    string Workspace,
    DateTimeOffset CreatedAt,
    string? Error = null)
{
    public string Summary()
    {
        var firstLine = Prompt.Split('\n').First().Trim();
        return firstLine.Length <= 60 ? firstLine : firstLine[..57] + "...";
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(WorkItem))]
internal sealed partial class VeloJsonContext : JsonSerializerContext;
