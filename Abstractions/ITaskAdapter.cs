namespace Velo.Abstractions;

public interface ITaskAdapter
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> AddTaskAsync(TaskItem taskItem, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskItem>> GetPendingAsync(int limit, CancellationToken cancellationToken = default);
    Task<bool> TryClaimAsync(string taskId, CancellationToken cancellationToken = default);
    Task CompleteAsync(string taskId, CancellationToken cancellationToken = default);
    Task FailAsync(string taskId, string reason, CancellationToken cancellationToken = default);
}
