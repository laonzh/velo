namespace Velo.Abstractions;

public interface IWorkItemAdapter
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkItem>> GetPendingAsync(int limit, CancellationToken cancellationToken = default);
    Task<bool> TryClaimAsync(string workItemId, CancellationToken cancellationToken = default);
    Task CompleteAsync(string workItemId, CancellationToken cancellationToken = default);
    Task FailAsync(string workItemId, string reason, CancellationToken cancellationToken = default);
}
