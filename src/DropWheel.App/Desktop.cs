using System.Runtime.InteropServices;
using DropWheel.Core.Actions;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace DropWheel.App;

/// <summary>The clipboard on the UI thread, and Explorer's rename box on an STA thread of its own.</summary>
internal sealed class Desktop(DispatcherQueue ui) : IDesktop
{
    /// <summary>CLIPBRD_E_CANT_OPEN: another program has the clipboard open.</summary>
    private const int ClipboardBusy = unchecked((int)0x800401D0);

    public async Task SetClipboardTextAsync(string text)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await OnUiAsync(() =>
                {
                    var data = new DataPackage();
                    data.SetText(text);
                    Clipboard.SetContent(data);
                    Clipboard.Flush(); // the text stays when DropWheel quits
                }).ConfigureAwait(false);
                return;
            }
            catch (COMException ex) when (ex.HResult == ClipboardBusy && attempt < 5)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    public Task StartRenameAsync(string path)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            if (Explorer.Rename(path) is { } problem)
            {
                done.SetException(new IOException($"资源管理器没能打开重命名（{problem}）"));
            }
            else
            {
                done.SetResult();
            }
        })
        { IsBackground = true, Name = "Rename" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private Task OnUiAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ui.TryEnqueue(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }))
        {
            done.SetException(new InvalidOperationException("The UI thread is gone."));
        }

        return done.Task;
    }
}
