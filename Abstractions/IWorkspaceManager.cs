namespace Velo.Abstractions;

public interface IWorkspaceManager
{
    Task<string> AcquireAsync(TaskItem taskItem, CancellationToken cancellationToken = default);
    Task ReleaseAsync(TaskItem taskItem, bool success, CancellationToken cancellationToken = default);
}