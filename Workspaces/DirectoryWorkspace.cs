using Velo.Abstractions;
using Velo.Configuration;

namespace Velo.Workspaces;

public sealed class DirectoryWorkspace() : IWorkspaceManager
{
    public Task<string> AcquireAsync(TaskItem taskItem, CancellationToken cancellationToken = default)
    {
        var workspacePath = Path.Combine(VeloConfig.WorkspacesDir, taskItem.Id);
        if (Directory.Exists(workspacePath))
        {
            throw new InvalidOperationException($"Workspace already exists: {workspacePath}");
        }
        Directory.CreateDirectory(workspacePath);
        return Task.FromResult(workspacePath);
    }

    public Task ReleaseAsync(TaskItem taskItem, bool success, CancellationToken cancellationToken = default)
    {
        var workspacePath = Path.Combine(VeloConfig.WorkspacesDir, taskItem.Id);
        if (!Directory.Exists(workspacePath))
        {
            throw new InvalidOperationException($"Workspace does not exist: {workspacePath}");
        }
        if (success)
        {
            Directory.Delete(workspacePath, true);
            return Task.CompletedTask;
        }
        Directory.Move(workspacePath, Path.Combine(VeloConfig.WorkspacesDir, $"{taskItem.Id}-failed"));
        return Task.CompletedTask;
    }
}