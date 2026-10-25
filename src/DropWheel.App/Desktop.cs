using System.Runtime.InteropServices;
using DropWheel.App.Diagnostics;
using DropWheel.Core.Actions;
using DropWheel.Core.Jobs;
using DropWheel.Shell;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace DropWheel.App;

/// <summary>
/// The clipboard on the UI thread; Explorer's rename box, the folder picker and the same-name question on
/// STA threads of their own.
/// </summary>
internal sealed class Desktop(DispatcherQueue ui) : IDesktop, IConflictPrompt
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

    public async Task<string?> PickFolderAsync(FolderPrompt prompt, CancellationToken cancellationToken)
    {
        FileLog.Info($"actions: asking for a folder: {prompt.Title}");
        var folder = await FolderDialog.PickAsync(prompt, cancellationToken).ConfigureAwait(false);
        FileLog.Info(folder is null ? "actions: no folder picked" : $"actions: picked {folder}");
        return folder;
    }

    public async Task<ConflictAnswer> AskAsync(string target, bool more, CancellationToken cancellationToken)
    {
        FileLog.Info($"conflict: asking about {target}{(more ? ", more to come" : "")}");
        var answer = await ConflictDialog.AskAsync(target, more, cancellationToken).ConfigureAwait(false);
        FileLog.Info($"conflict: answered {answer.Policy}{(answer.ForTheRest ? " for the rest" : "")}");
        return answer;
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
