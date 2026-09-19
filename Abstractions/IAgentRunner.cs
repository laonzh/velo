namespace Velo.Abstractions;

public interface IAgentRunner
{
    string Name { get; }
    Task<AgentRunResult> RunAsync(AgentRunContext context, CancellationToken cancellationToken = default);
}