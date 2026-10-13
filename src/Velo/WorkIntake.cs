namespace Velo;

// Adds work for both the CLI and the HTTP API. A worktree that cannot be recorded is removed again.
internal sealed class WorkIntake(WorkQueue queue, Workspace workspace)
{
    public async Task<string> AddAsync(string prompt, string directory, bool worktree)
    {
        var id = WorkQueue.NewId();
        var checkout = worktree
            ? await workspace.CreateAsync(id, directory)
            : null;
        try
        {
            queue.Add(id, prompt, checkout?.Path ?? directory, checkout);
        }
        catch
        {
            if (checkout is not null) await workspace.RemoveAsync(checkout);
            throw;
        }
        return id;
    }
}
