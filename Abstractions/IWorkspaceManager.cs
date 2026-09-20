namespace Velo.Abstractions;

public interface IWorkspaceManager
{
    Task<string> AcquireAsync(WorkItem workItem, CancellationToken cancellationToken = default);
    Task ReleaseAsync(WorkItem workItem, bool success, CancellationToken cancellationToken = default);
}