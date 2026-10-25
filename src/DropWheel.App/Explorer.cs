using Windows.Win32;

namespace DropWheel.App;

/// <summary>File Explorer as a place to send the user.</summary>
internal static class Explorer
{
    /// <summary>Opens the folder holding <paramref name="path"/> with it selected, reusing a window already showing that folder.</summary>
    /// <returns>Null once Explorer has been asked, otherwise why it could not be.</returns>
    public static unsafe string? Select(string path)
    {
        var item = PInvoke.ILCreateFromPath(path);
        if (item is null)
        {
            return "no item ID for it";
        }

        try
        {
            var result = PInvoke.SHOpenFolderAndSelectItems(item, 0, null, 0);
            return result.Failed ? $"0x{result.Value:X8}" : null;
        }
        finally
        {
            PInvoke.ILFree(item);
        }
    }
}
