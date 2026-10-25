namespace DropWheel.Core.Actions;

/// <summary>The parts of the desktop a file action needs that are not file operations.</summary>
public interface IDesktop
{
    Task SetClipboardTextAsync(string text);

    /// <summary>Shows <paramref name="path"/> in Explorer with its name open for editing.</summary>
    Task StartRenameAsync(string path);

    /// <summary>Asks for a folder in front of everything; null when the user cancels or picks no folder on disk.</summary>
    /// <param name="cancellationToken">Closes the dialog as if canceled.</param>
    Task<string?> PickFolderAsync(FolderPrompt prompt, CancellationToken cancellationToken);
}

/// <param name="Title">The dialog's title, such as "把 a.jpg 移动到".</param>
/// <param name="OkLabel">The button that picks the folder.</param>
/// <param name="StartFolder">Where the dialog opens the first time; after that, wherever it was last left.</param>
/// <param name="Memory">Dialogs that share this id share the folder Windows remembers.</param>
public sealed record FolderPrompt(string Title, string OkLabel, string StartFolder, Guid Memory);
