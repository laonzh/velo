namespace Velo.Abstractions;

public interface IScheduler
{
    Task StartAsync(CancellationToken cancellationToken = default);
}