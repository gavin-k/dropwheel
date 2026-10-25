using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    private const string ConflictTitle = "DropWheel 同名文件";

    // TDM_CLICK_BUTTON and the question's button ids, from DropWheel.Shell.ConflictDialog.
    private const uint ClickButton = 0x0400 + 102;
    private const int KeepBoth = 102;
    private const int SkipIt = 103;

    /// <summary>
    /// The same-name policies, after <see cref="ChangeSettings"/> sent outputs to one folder as {name}-dw:
    /// with 替换 the PNG takes the place of a file of its name, which goes to the Recycle Bin; with 每次询问
    /// the question comes up in front, 保留两个 numbers the new PNG and 跳过 writes nothing. 自动编号 is set
    /// back at the end, even after a failed step.
    /// </summary>
    private static void SameName(Checks checks, AppLog log, int app, string folder, bool explorer)
    {
        try
        {
            SameNameSteps(checks, log, app, folder, explorer);
        }
        finally
        {
            SetConflict(checks, log, "自动编号，例如 photo (1).png", "AutoNumber");
        }
    }

    private static void SameNameSteps(Checks checks, AppLog log, int app, string folder, bool explorer)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "same");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir))
        {
            File.Delete(old);
        }

        var jpg = Path.Combine(dir, $"same-{DateTime.Now:HHmmss}.jpg");
        WriteJpeg(jpg, 320, 200);
        var name = $"{Path.GetFileNameWithoutExtension(jpg)}-dw";
        var taken = Path.Combine(folder, $"{name}.png");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(taken, new byte[16]);

        if (!SetConflict(checks, log, "替换，旧的移到回收站", "OverwriteToRecycleBin"))
        {
            return;
        }

        var png = ConvertThroughWheel(checks, log, jpg, "png", explorer, [VIRTUAL_KEY.VK_LCONTROL]);
        var replaced = log.WaitFor(2000, "queue: ", " replaced 1, ");
        var what = "no output";
        var real = png is not null && IsPng(taken, 320, 200, out what);
        var binned = FileOpsScenario.InRecycleBin(folder).Contains(name);
        checks.Check("替换 puts the PNG in place of the file of its name and moves that file to the Recycle Bin",
            string.Equals(png, taken, StringComparison.OrdinalIgnoreCase) && real && binned && replaced is not null,
            $"{png ?? "no output"}: {what}; old file {(binned ? "in" : "not in")} the Recycle Bin; {replaced ?? "no 'replaced 1' line"}");
        if (!real || !SetConflict(checks, log, "每次询问", "Ask"))
        {
            return;
        }

        var before = File.ReadAllBytes(taken);
        var numbered = Path.Combine(folder, $"{name} (1).png");
        var kept = Answer(checks, log, app, jpg, explorer, KeepBoth);
        var asked = log.Find($"conflict: asking about {taken}");
        var answered = log.Find("conflict: answered AutoNumber");
        what = "not there";
        var both = File.Exists(numbered) && IsPng(numbered, 320, 200, out what);
        checks.Check("每次询问 asks about the taken name, and 保留两个 numbers the new PNG and leaves the old one",
            kept?.Contains(" done", StringComparison.Ordinal) == true && asked is not null && answered is not null && both && File.ReadAllBytes(taken).SequenceEqual(before),
            $"{asked ?? "no 'asking about' line"}; {answered ?? "no answer line"}; {kept ?? "no queue outcome within 10 s"}; {Path.GetFileName(numbered)}: {what}");

        var skipped = Answer(checks, log, app, jpg, explorer, SkipIt);
        answered = log.Find("conflict: answered Skip");
        var extra = Directory.GetFiles(folder, $"{name}*").Length;
        checks.Check("跳过 in the question skips the job and writes nothing",
            skipped?.Contains(" skipped: ", StringComparison.Ordinal) == true && answered is not null && extra == 2 && File.ReadAllBytes(taken).SequenceEqual(before),
            $"{answered ?? "no answer line"}; {skipped ?? "no queue outcome within 10 s"}; {extra} file(s) named {name}*");
    }

    /// <summary>Drops <paramref name="jpg"/> on PNG with Ctrl and presses <paramref name="button"/> in the question that comes up.</summary>
    /// <returns>The queue's line for the job, or null.</returns>
    private static string? Answer(Checks checks, AppLog log, int app, string jpg, bool explorer, int button)
    {
        if (DropOnWheel(checks, log, jpg, "png", explorer, [VIRTUAL_KEY.VK_LCONTROL], afterDrop: _ =>
        {
            var dialog = Picker(checks, app, ConflictTitle, "the same-name question");
            if (!dialog.IsNull)
            {
                PInvoke.PostMessage(dialog, ClickButton, new WPARAM((nuint)button), new LPARAM(0));
                Checks.Wait(() => !PInvoke.IsWindow(dialog), 3000);
            }
        }) is not { } label)
        {
            return null;
        }

        return log.WaitFor(10000, $"queue: {label} ");
    }

    /// <summary>Picks <paramref name="option"/> under 同名文件已经存在时 in the settings window, which is then closed.</summary>
    private static bool SetConflict(Checks checks, AppLog log, string option, string policy)
    {
        log.Mark();
        var opened = Invoke(QueueTitle, "设置", out var how);
        var window = default(HWND);
        opened = opened && Checks.Wait(() => !(window = PInvoke.FindWindow(null, SettingsTitle)).IsNull && PInvoke.IsWindowVisible(window), 5000);
        var saved = opened && Pick("conflict", option, out how) ? log.WaitFor(3000, $"settings: saved conflict {policy}") : null;
        if (!window.IsNull)
        {
            PInvoke.PostMessage(window, PInvoke.WM_CLOSE, new WPARAM(0), new LPARAM(0));
            Checks.Wait(() => !PInvoke.IsWindowVisible(window), 3000);
        }

        return checks.Check($"picking '{option}' saves {policy}", saved is not null, saved ?? how);
    }

    /// <summary>Opens the combo box with id <paramref name="id"/> in the settings window and selects its item named <paramref name="option"/>.</summary>
    private static bool Pick(string id, string option, out string how)
    {
        if (Element(SettingsTitle, id, out how, byId: true) is not { } combo)
        {
            return false;
        }

        var list = (IUIAutomationExpandCollapsePattern)combo.GetCurrentPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId);
        list.Expand();
        var uia = (IUIAutomation)new CUIAutomation8();
        var named = uia.CreatePropertyCondition(UIA_PROPERTY_ID.UIA_NamePropertyId, option);
        IUIAutomationElement? item = null;
        Checks.Wait(() => (item = combo.FindFirst(TreeScope.TreeScope_Descendants, named)) is not null, 3000);
        if (item is null)
        {
            how = $"no item '{option}' under '{id}'";
            return false;
        }

        ((IUIAutomationSelectionItemPattern)item.GetCurrentPattern(UIA_PATTERN_ID.UIA_SelectionItemPatternId)).Select();
        if (list.CurrentExpandCollapseState != ExpandCollapseState.ExpandCollapseState_Collapsed)
        {
            list.Collapse();
        }

        how = $"selected '{option}'";
        return true;
    }
}
