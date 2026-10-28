namespace Velo;

/// <summary>
/// Runs a single turn on <c>codex app-server</c> (JSON-RPC lines over stdio). Never throws for runtime problems.
/// </summary>
/// <param name="codexPath">Path to the codex executable; null = <c>codex</c> on PATH.</param>
public sealed class CodexAgent(string? codexPath) : IAgent
{
    internal static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30); // no output at all from the process
    internal static TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(60); // initialize + thread/start + turn/start

    public async Task<AgentResult> RunAsync(AgentRequest request, Action<string> log, CancellationToken ct)
    {
        var session = new CodexSession(request, codexPath, log, ct);
        try
        {
            return await session.ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException)
        {
            return session.Result(AgentStatus.Cancelled, null);
        }
        catch (Exception ex)
        {
            return session.Result(AgentStatus.Failed, ex is CodexSession.Failure ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            session.Cleanup();
        }
    }

    public async Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
    {
        var request = new AgentRequest { Prompt = "", Directory = Path.GetTempPath() };
        var session = new CodexSession(request, codexPath, _ => { }, ct);
        try
        {
            return await session.ListModelsAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CodexSession.Failure or OperationCanceledException or IOException)
        {
            return [];
        }
        finally
        {
            session.Cleanup();
        }
    }

    public string? TakeOver(string sessionId) => $"codex resume {sessionId}";
}
