using Velo.Adapters;
using Velo.Configuration;
using Velo.Runners;
using Velo.Workspaces;

namespace Velo.Orchestration;

public sealed class VeloEngine
{
    private readonly VeloConfig _config;

    public VeloEngine(VeloConfig config)
    {
        _config = config;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var taskAdapter = new MemoryTaskAdapter();
        var agentRunner = new CodexRunner();
        var workspaceManager = new DirectoryWorkspace();
        var scheduler = new TickScheduler(_config, taskAdapter, agentRunner, workspaceManager);
        await scheduler.StartAsync(cancellationToken).ConfigureAwait(false);
    }
}