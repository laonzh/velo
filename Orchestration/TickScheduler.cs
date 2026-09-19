using Velo.Abstractions;

namespace Velo.Orchestration;

public sealed class TickScheduler : IScheduler
{
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}