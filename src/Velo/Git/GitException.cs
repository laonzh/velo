namespace Velo;

/// <summary>A git command failed; the message carries git's output.</summary>
public class GitException(string message) : Exception(message);

/// <summary>
/// A merge stopped on conflicts; <see cref="Files"/> are the conflicted paths (the merge itself has been aborted).
/// </summary>
public sealed class MergeConflictException(IReadOnlyList<string> files, string message) : GitException(message)
{
    public IReadOnlyList<string> Files { get; } = files;
}
