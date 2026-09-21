using Velo.Abstractions;
using Velo.Common;
using Velo.Configuration;

namespace Velo.Orchestration;

public sealed class TickScheduler : IScheduler
{
    private readonly VeloConfig _config;
    private readonly SemaphoreSlim _gate;

    private readonly ITaskAdapter _taskAdapter;
    private readonly IAgentRunner _agentRunner;
    private readonly IWorkspaceManager _workspaceManager;

    public TickScheduler(VeloConfig config, ITaskAdapter taskAdapter, IAgentRunner agentRunner, IWorkspaceManager workspaceManager)
    {
        _config = config;
        _gate = new SemaphoreSlim(config.MaxConcurrency, config.MaxConcurrency);
        _taskAdapter = taskAdapter;
        _agentRunner = agentRunner;
        _workspaceManager = workspaceManager;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var timer = new PeriodicTimer(_config.PollingInterval);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await ProcessTickAsync(cancellationToken).ConfigureAwait(false);
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            timer.Dispose();
            _gate.Dispose();
        }
    }

    private async Task ProcessTickAsync(CancellationToken cancellationToken = default)
    {
        if (_gate.CurrentCount <= 0) return;
        var tasks = await _taskAdapter.GetPendingAsync(_gate.CurrentCount, cancellationToken).ConfigureAwait(false);
        foreach (var task in tasks)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ok = await _taskAdapter.TryClaimAsync(task.Id, cancellationToken).ConfigureAwait(false);
                if (!ok) continue;
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

                var logFilePath = Path.Combine(VeloConfig.LogsDir, $"{task.Id}.log");
                var logWriter = new FileLog(logFilePath);
                var workspacePath = await _workspaceManager.AcquireAsync(task, cancellationToken).ConfigureAwait(false);
                var result = await _agentRunner.RunAsync(new AgentRunContext(task.Id, task.Title, task.Description, workspacePath, logWriter), cancellationToken).ConfigureAwait(false);

                if (result.Success)
                {
                    await _taskAdapter.CompleteAsync(task.Id, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _taskAdapter.FailAsync(task.Id, result.ErrorMessage ?? "Unknown error", cancellationToken).ConfigureAwait(false);
                }
                await _workspaceManager.ReleaseAsync(task, result.Success, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _taskAdapter.FailAsync(task.Id, "Unknown error", cancellationToken).ConfigureAwait(false);
                await _workspaceManager.ReleaseAsync(task, false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}