namespace Velo.Abstractions;

public interface IWorkspaceManager
{
    Task<string> AcquireAsync(string workItemId, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken = default);
    Task ReleaseAsync(string workItemId, CancellationToken cancellationToken = default);
}