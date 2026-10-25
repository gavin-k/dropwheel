using System.Runtime.InteropServices;
using DropWheel.Core.Actions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;

namespace DropWheel.Shell;

/// <summary>
/// The system folder picker on an STA thread of its own, with no owner: DropWheel has no window of its
/// own in front when a drop lands. Windows keeps the foreground from a process the user is not working
/// in, so an empty input event right before the dialog opens lets it come up in front of Explorer.
/// </summary>
public static class FolderDialog
{
    /// <summary>HRESULT_FROM_WIN32(ERROR_CANCELLED): the user closed the dialog.</summary>
    private const int Canceled = unchecked((int)0x800704C7);

    /// <summary>The picked folder, or null when the user closes the dialog. Canceling closes it too.</summary>
    public static Task<string?> PickAsync(FolderPrompt prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var thread = PInvoke.GetCurrentThreadId();
                string? folder;
                using (cancellationToken.Register(() => Close(thread)))
                {
                    folder = Pick(prompt, cancellationToken);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    done.SetCanceled(cancellationToken);
                }
                else
                {
                    done.SetResult(folder);
                }
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "FolderDialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static unsafe string? Pick(FolderPrompt prompt, CancellationToken cancellationToken)
    {
        PInvoke.CoCreateInstance(typeof(FileOpenDialog).GUID, null, CLSCTX.CLSCTX_INPROC_SERVER, out IFileOpenDialog dialog).ThrowOnFailure();
        try
        {
            // The client GUID comes first: it picks the remembered folder the rest of the setup starts from.
            dialog.SetClientGuid(prompt.Memory);
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM
                | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST | FILEOPENDIALOGOPTIONS.FOS_NOCHANGEDIR);
            if (Directory.Exists(prompt.StartFolder))
            {
                dialog.SetDefaultFolder(Item(prompt.StartFolder));
            }

            dialog.SetTitle(prompt.Title);
            dialog.SetOkButtonLabel(prompt.OkLabel);
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            // A zero mouse event moves and clicks nothing, but makes this process the one that sent the
            // last input, which Windows lets bring a window to the front.
            PInvoke.SendInput([new INPUT { type = INPUT_TYPE.INPUT_MOUSE }], sizeof(INPUT));
            try
            {
                dialog.Show(HWND.Null);
            }
            catch (COMException ex) when (ex.HResult == Canceled)
            {
                return null;
            }

            dialog.GetResult(out var item);
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var name);
            try
            {
                return name.ToString();
            }
            finally
            {
                Marshal.FreeCoTaskMem((nint)name.Value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    /// <summary>Closes the dialog the way its 取消 button does.</summary>
    private static void Close(uint thread) => PInvoke.EnumThreadWindows(thread, (window, _) =>
    {
        if (ClassName(window) == "#32770")
        {
            PInvoke.PostMessage(window, PInvoke.WM_CLOSE, 0, 0);
        }

        return true;
    }, 0);

    private static unsafe string ClassName(HWND window)
    {
        Span<char> name = stackalloc char[16];
        fixed (char* p = name)
        {
            return new string(p, 0, PInvoke.GetClassName(window, p, name.Length));
        }
    }

    private static IShellItem Item(string path)
    {
        PInvoke.SHCreateItemFromParsingName(Path.GetFullPath(path), null, typeof(IShellItem).GUID, out var item).ThrowOnFailure();
        return (IShellItem)item;
    }
}
