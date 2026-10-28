namespace Velo;

public sealed record Settings
{
    public int Port { get; init; } = 4870;
    public int MaxConcurrency { get; init; } = 2;
    public string Agent { get; init; } = "codex"; // see AgentRegistry
    public Dictionary<string, AgentOptions>? Agents { get; init; } // per-agent options by name
    public string? CodexPath { get; init; } // legacy: path of the codex agent when agents.codex.path is not set
    public string? Model { get; init; }
    public string? Effort { get; init; }
}

public sealed record AgentOptions
{
    public string? Path { get; init; } // the agent's executable; null = found on PATH
}
