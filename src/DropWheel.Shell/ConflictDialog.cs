using System.Runtime.InteropServices;
using DropWheel.Core.Jobs;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropWheel.Shell;

/// <summary>
/// Asks what to do with an output whose name is taken: a task dialog on an STA thread of its own with no
/// owner, brought to the front the way <see cref="FolderDialog"/> is. Closing it skips the item; canceling
/// the job closes it. Task dialogs need Common Controls 6, which the app's manifest asks for.
/// </summary>
public static class ConflictDialog
{
    public const string Title = "DropWheel 同名文件";

    // Button ids, which TDM_CLICK_BUTTON presses from outside.
    public const int Replace = 101;
    public const int KeepBoth = 102;
    public const int Skip = 103;

    public static Task<ConflictAnswer> AskAsync(string target, bool more, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource<ConflictAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var thread = PInvoke.GetCurrentThreadId();
                ConflictAnswer answer;
                using (cancellationToken.Register(() => FolderDialog.Close(thread)))
                {
                    answer = Ask(target, more);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    done.SetCanceled(cancellationToken);
                }
                else
                {
                    done.SetResult(answer);
                }
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "ConflictDialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static unsafe ConflictAnswer Ask(string target, bool more)
    {
        var kind = Directory.Exists(target) ? "文件夹" : "文件";
        var instruction = $"「{Path.GetFileName(target)}」已经存在";
        var content = $"位置：{Path.GetDirectoryName(target)}\n替换会把原来的{kind}移到回收站，之后撤销这个任务也不会把它移回来。保留两个会给新的加上编号。";
        fixed (char* title = Title, main = instruction, text = content, replace = "替换", keep = "保留两个", skip = "跳过", rest = "后面的同名项也这样处理")
        {
            var buttons = stackalloc TASKDIALOG_BUTTON[]
            {
                new() { nButtonID = Replace, pszButtonText = replace },
                new() { nButtonID = KeepBoth, pszButtonText = keep },
                new() { nButtonID = Skip, pszButtonText = skip },
            };
            var config = new TASKDIALOGCONFIG
            {
                cbSize = (uint)Marshal.SizeOf<TASKDIALOGCONFIG>(), // marshaled: the callback field is a delegate
                dwFlags = TASKDIALOG_FLAGS.TDF_ALLOW_DIALOG_CANCELLATION,
                pszWindowTitle = title,
                pszMainInstruction = main,
                pszContent = text,
                cButtons = 3,
                pButtons = buttons,
                nDefaultButton = KeepBoth,
                pszVerificationText = more ? rest : null,
            };

            // See FolderDialog: the empty input event lets the dialog come up in front.
            PInvoke.SendInput([new INPUT { type = INPUT_TYPE.INPUT_MOUSE }], sizeof(INPUT));
            int pressed;
            BOOL forTheRest;
            PInvoke.TaskDialogIndirect(in config, &pressed, null, &forTheRest).ThrowOnFailure();
            return pressed switch
            {
                Replace => new(ConflictPolicy.OverwriteToRecycleBin, forTheRest),
                KeepBoth => new(ConflictPolicy.AutoNumber, forTheRest),
                Skip => new(ConflictPolicy.Skip, forTheRest),
                _ => new(ConflictPolicy.Skip), // closed: this item only
            };
        }
    }
}
