namespace DropWheel.Core.Actions;

/// <summary>The parts of the desktop a file action needs that are not file operations.</summary>
public interface IDesktop
{
    Task SetClipboardTextAsync(string text);

    /// <summary>Shows <paramref name="path"/> in Explorer with its name open for editing.</summary>
    Task StartRenameAsync(string path);
}
