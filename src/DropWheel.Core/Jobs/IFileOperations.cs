namespace DropWheel.Core.Jobs;

/// <summary>File operations that must go through the Shell so the user can see and reverse them.</summary>
public interface IFileOperations
{
    /// <summary>Send files to the Recycle Bin. Never a permanent delete; files that are already gone are ignored.</summary>
    Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Moves each item to its full destination path, as one operation.</summary>
    Task MoveAsync(IReadOnlyList<FileMove> moves, CancellationToken cancellationToken = default);
}
