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

    /// <summary>
    /// Selects <paramref name="path"/> in its folder with the name open for editing. Explorer only puts a
    /// name in edit mode when it is handed as a child of the folder, not as one full item ID.
    /// </summary>
    /// <returns>Null once Explorer has been asked, otherwise why it could not be.</returns>
    public static unsafe string? Rename(string path)
    {
        const uint OFASI_EDIT = 0x0001;
        var folder = PInvoke.ILCreateFromPath(Path.GetDirectoryName(path));
        var item = PInvoke.ILCreateFromPath(path);
        try
        {
            if (folder is null || item is null)
            {
                return "no item ID for it";
            }

            var child = PInvoke.ILFindLastID(item);
            var result = PInvoke.SHOpenFolderAndSelectItems(folder, 1, &child, OFASI_EDIT);
            return result.Failed ? $"0x{result.Value:X8}" : null;
        }
        finally
        {
            PInvoke.ILFree(item);
            PInvoke.ILFree(folder);
        }
    }
}
