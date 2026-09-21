namespace Velo.Abstractions;

public interface ITaskAdapter
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskItem>> GetPendingAsync(int limit, CancellationToken cancellationToken = default);
    Task<bool> TryClaimAsync(TaskItem taskItem, CancellationToken cancellationToken = default);
    Task CompleteAsync(TaskItem taskItem, CancellationToken cancellationToken = default);
    Task FailAsync(TaskItem taskItem, string reason, CancellationToken cancellationToken = default);
}
