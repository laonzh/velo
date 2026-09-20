namespace Velo.Abstractions;

public interface IWorkItemAdapter
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkItem>> GetPendingAsync(int limit, CancellationToken cancellationToken = default);
    Task<bool> TryClaimAsync(WorkItem workItem, CancellationToken cancellationToken = default);
    Task CompleteAsync(WorkItem workItem, CancellationToken cancellationToken = default);
    Task FailAsync(WorkItem workItem, string reason, CancellationToken cancellationToken = default);
}
