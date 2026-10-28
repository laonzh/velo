namespace Velo;

/// <summary>Content of state.json.</summary>
public sealed class State
{
    public string HomeId { get; init; } = "";
    public int NextNumber { get; set; } = 1;
    public List<Issue> Issues { get; set; } = [];
}
