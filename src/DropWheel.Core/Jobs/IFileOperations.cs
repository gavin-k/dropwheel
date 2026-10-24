namespace DropWheel.Core.Jobs;

/// <summary>File operations that must go through the Shell so the user can see and reverse them.</summary>
public interface IFileOperations
{
    /// <summary>Send files to the Recycle Bin. Never a permanent delete; files that are already gone are ignored.</summary>
    Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default);
}
