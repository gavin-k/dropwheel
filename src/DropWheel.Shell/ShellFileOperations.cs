using DropWheel.Core.Jobs;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace DropWheel.Shell;

/// <summary>
/// <see cref="IFileOperations"/> through IFileOperation, so undo shows up in the Recycle Bin and in
/// Explorer's own undo history. Each call runs on a fresh STA thread: IFileOperation is apartment-bound
/// and may show Shell UI (the "can't recycle, delete permanently?" prompt).
/// </summary>
public sealed class ShellFileOperations : IFileOperations
{
    public Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0)
        {
            return Task.CompletedTask;
        }

        // FOF_WANTNUKEWARNING overrides FOF_NOCONFIRMATION for files that would be deleted outright
        // (too big for the bin, network share), so nothing is ever destroyed without the user saying so.
        const FILEOPERATION_FLAGS flags = FILEOPERATION_FLAGS.FOF_ALLOWUNDO | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION
            | FILEOPERATION_FLAGS.FOF_SILENT | FILEOPERATION_FLAGS.FOF_NOERRORUI | FILEOPERATION_FLAGS.FOF_WANTNUKEWARNING
            | FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE;
        return RunAsync(flags, op =>
        {
            foreach (var path in existing)
            {
                op.DeleteItem(Item(path), null);
            }
        }, $"recycle {existing.Count} item(s)", cancellationToken);
    }

    public Task MoveAsync(IReadOnlyList<FileMove> moves, CancellationToken cancellationToken = default) =>
        TransferAsync(moves, copy: false, cancellationToken);

    public Task CopyAsync(IReadOnlyList<FileMove> copies, CancellationToken cancellationToken = default) =>
        TransferAsync(copies, copy: true, cancellationToken);

    private static Task TransferAsync(IReadOnlyList<FileMove> items, bool copy, CancellationToken cancellationToken)
    {
        // No FOF_NOCONFIRMATION here: it would answer "replace" for a file now sitting at the destination.
        const FILEOPERATION_FLAGS flags = FILEOPERATION_FLAGS.FOF_ALLOWUNDO | FILEOPERATION_FLAGS.FOF_NOCONFIRMMKDIR
            | FILEOPERATION_FLAGS.FOF_RENAMEONCOLLISION | FILEOPERATION_FLAGS.FOF_SILENT;
        return RunAsync(flags, op =>
        {
            foreach (var item in items)
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(item.To)) ?? throw new ArgumentException($"No folder in {item.To}.", nameof(items));
                Directory.CreateDirectory(folder);
                if (copy)
                {
                    op.CopyItem(Item(item.From), Item(folder), Path.GetFileName(item.To), null);
                }
                else
                {
                    op.MoveItem(Item(item.From), Item(folder), Path.GetFileName(item.To), null);
                }
            }
        }, $"{(copy ? "copy" : "move")} {items.Count} item(s)", cancellationToken);
    }

    private static Task RunAsync(FILEOPERATION_FLAGS flags, Action<IFileOperation> queue, string what, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                PInvoke.CoCreateInstance(typeof(FileOperation).GUID, null, CLSCTX.CLSCTX_ALL, out IFileOperation op).ThrowOnFailure();
                op.SetOperationFlags(flags);
                queue(op);
                op.PerformOperations();
                op.GetAnyOperationsAborted(out var aborted);
                if (aborted)
                {
                    throw new IOException($"The Shell did not finish: {what}.");
                }

                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "ShellFileOperations" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static IShellItem Item(string path)
    {
        PInvoke.SHCreateItemFromParsingName(Path.GetFullPath(path), null, typeof(IShellItem).GUID, out var item).ThrowOnFailure();
        return (IShellItem)item;
    }
}
