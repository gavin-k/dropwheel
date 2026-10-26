using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Actions;

/// <summary>The Actions wheel's items, in sector order.</summary>
public static class FileActions
{
    public static IReadOnlyList<IWheelOperation> Create(IFileOperations files, IDesktop desktop, TimeProvider? time = null) =>
    [
        new CopyPathAction(desktop),
        new TransferAction(copy: false, files, desktop),
        new TransferAction(copy: true, files, desktop),
        new RenameAction(desktop),
        new CompressZipAction(time),
        new RecycleAction(files),
    ];

    /// <summary>Unavailable when an item is gone or is a whole drive, which no action works on.</summary>
    internal static Availability CheckItems(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (IsDriveRoot(path))
            {
                return Availability.Unavailable(L.T("不能用于整个磁盘")) with { Cause = UnavailableCause.SelectionShape };
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return Availability.Unavailable(L.T("找不到 {0}", Path.GetFileName(path)));
            }
        }

        return Availability.Available;
    }

    internal static bool IsDriveRoot(string path) =>
        Path.GetPathRoot(path) is { Length: > 0 } root && Path.TrimEndingDirectorySeparator(path).Length <= Path.TrimEndingDirectorySeparator(root).Length;
}
