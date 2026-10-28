namespace Velo;

/// <summary>
/// The agents Velo knows, by name (<c>agent</c> in config.json). A hard-coded table: no reflection, AOT-safe.
/// </summary>
public sealed class AgentRegistry(Settings settings)
{
    private readonly Dictionary<string, IAgent> _agents = new(StringComparer.Ordinal)
    {
        // agents.codex.path, else the legacy top-level codexPath, else codex on PATH
        ["codex"] = new CodexAgent(settings.Agents?.GetValueOrDefault("codex")?.Path ?? settings.CodexPath),
    };

    /// <summary>The agent called <paramref name="name"/>, or null.</summary>
    public IAgent? Find(string? name) => name is not null && _agents.TryGetValue(name, out IAgent? agent) ? agent : null;

    /// <summary>The agent called <paramref name="name"/>; 400 when there is none.</summary>
    public IAgent Get(string? name) =>
        Find(name) ?? throw VeloException.BadRequest($"unknown agent '{name}' (available: {string.Join(", ", _agents.Keys)})");
}
