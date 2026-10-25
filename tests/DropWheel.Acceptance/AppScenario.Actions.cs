using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    /// <summary>
    /// The Actions wheel, opened by a Win tap mid-drag that must not open Start: 复制路径 puts the quoted path
    /// on the clipboard, 压缩 packs a folder with its tree into a zip next to it, 删除 is off until it is
    /// ticked in settings and then moves the file to the Recycle Bin, and 重命名 opens Explorer's name box
    /// on the file. 移动到 and 复制到 ask for a folder in front of everything; undo moves the file back or
    /// recycles the copy, and closing the picker leaves the file alone.
    /// </summary>
    private static void FileActions(Checks checks, AppLog log, int app, bool explorer)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "actions");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir))
        {
            File.Delete(old);
        }

        foreach (var old in Directory.GetDirectories(dir))
        {
            Directory.Delete(old, recursive: true);
        }

        var stamp = DateTime.Now.ToString("HHmmss");
        try
        {
            CopyPath(checks, log, Write(Path.Combine(dir, $"path-{stamp}.txt")), explorer);
            Zip(checks, log, dir, stamp, explorer);
            Recycle(checks, log, Write(Path.Combine(dir, $"bin-{stamp}.txt")), explorer);
            Rename(checks, log, Write(Path.Combine(dir, $"name-{stamp}.txt")), explorer);
            var folder = Directory.CreateDirectory(Path.Combine(dir, $"to-{stamp}")).FullName;
            Transfer(checks, log, app, Write(Path.Combine(dir, $"move-{stamp}.txt")), folder, copy: false, explorer);
            Transfer(checks, log, app, Write(Path.Combine(dir, $"copy-{stamp}.txt")), folder, copy: true, explorer);
            ClosePicker(checks, log, app, Write(Path.Combine(dir, $"stay-{stamp}.txt")), explorer);
        }
        finally
        {
            // A Win tap that got through leaves Start open over everything the next scenario does.
            if (StartMenu().Open)
            {
                PressAndRelease(VIRTUAL_KEY.VK_LWIN);
                Thread.Sleep(500);
                Log.Info($"actions: closed Start after the scenario; foreground now {ForegroundOwner()}");
            }
        }
    }

    private static void CopyPath(Checks checks, AppLog log, string file, bool explorer)
    {
        // The clipboard is not cleared first: that would make this process its owner, and the app's copy
        // waits up to 5 s for an owner that is not pumping messages. The path carries this run's time.
        (bool Open, string Owner)? start = null;
        string? entered = null;
        var label = DropOnWheel(checks, log, file, "action.copy-path", explorer, [VIRTUAL_KEY.VK_LWIN], beforeRelease: () =>
        {
            start = StartMenu();
            entered = log.Find("file(s) entered");
        });
        checks.Check("a Win tap mid-drag opens the Actions wheel and not Start", entered is not null && start is { Open: false }, $"foreground {start?.Owner ?? "not looked at"}; {entered ?? "no 'file(s) entered' line"}");
        checks.Check("the Actions wheel offers 删除 greyed out until it is ticked in settings", SlotOf(entered, "action.recycle(off)") >= 0, entered ?? "no 'file(s) entered' line");
        if (label is null)
        {
            return;
        }

        var done = log.WaitFor(10000, $"queue: {label} ");
        var text = ClipboardText();
        checks.Check("复制路径 puts the quoted path on the clipboard", done?.Contains(" done", StringComparison.Ordinal) == true && string.Equals(text, $"\"{file}\"", StringComparison.OrdinalIgnoreCase),
            $"{done ?? "no queue outcome within 10 s"}; clipboard {(text is null ? "has no text" : $"'{text}'")}");
    }

    private static void Zip(Checks checks, AppLog log, string dir, string stamp, bool explorer)
    {
        var folder = Path.Combine(dir, $"pack-{stamp}");
        Write(Path.Combine(folder, "a.txt"));
        Write(Path.Combine(folder, "sub", "b.txt"));
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        if (DropOnWheel(checks, log, folder, "action.zip", explorer, [VIRTUAL_KEY.VK_LWIN]) is not { } label)
        {
            return;
        }

        var done = log.WaitFor(30000, $"queue: {label} ");
        var output = done is null ? null : OutputsRegex().Match(done) is { Success: true } m ? m.Groups[1].Value : null;
        var expected = folder + ".zip";
        var entries = "";
        if (output is not null && File.Exists(output))
        {
            using var zip = ZipFile.OpenRead(output);
            entries = string.Join(", ", zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }

        var name = Path.GetFileName(folder);
        checks.Check("压缩 packs the folder, its tree and its empty folder into a zip next to it",
            string.Equals(output, expected, StringComparison.OrdinalIgnoreCase) && entries == $"{name}/, {name}/a.txt, {name}/empty/, {name}/sub/, {name}/sub/b.txt" && Directory.Exists(folder),
            $"{done ?? "no queue outcome within 30 s"}; entries [{entries}]; folder still there={Directory.Exists(folder)}");
    }

    private static void Recycle(Checks checks, AppLog log, string file, bool explorer)
    {
        log.Mark();
        var opened = Invoke(QueueTitle, "设置", out var how);
        var window = default(HWND);
        opened = opened && Checks.Wait(() => !(window = PInvoke.FindWindow(null, SettingsTitle)).IsNull && PInvoke.IsWindowVisible(window), 5000);
        Toggle("删除（回收站）");
        var saved = opened ? log.WaitFor(2000, "settings: saved file action DeleteToRecycleBin on") : null;
        PInvoke.PostMessage(window, PInvoke.WM_CLOSE, new WPARAM(0), new LPARAM(0));
        if (!checks.Check("ticking 删除（回收站） in settings turns the Delete action on", saved is not null, saved ?? $"{how}; no 'saved file action' line"))
        {
            return;
        }

        if (DropOnWheel(checks, log, file, "action.recycle", explorer, [VIRTUAL_KEY.VK_LWIN]) is not { } label)
        {
            return;
        }

        var done = log.WaitFor(10000, $"queue: {label} ");
        Checks.Wait(() => !File.Exists(file), 3000);
        var binned = FileOpsScenario.InRecycleBin(Path.GetDirectoryName(file)!);
        checks.Check("删除 moves the file to the Recycle Bin", done?.Contains(" done", StringComparison.Ordinal) == true && !File.Exists(file) && binned.Contains(Path.GetFileNameWithoutExtension(file)),
            $"{done ?? "no queue outcome within 10 s"}; exists={File.Exists(file)}; Recycle Bin items from the folder: [{string.Join(", ", binned)}]");
    }

    private static void Rename(Checks checks, AppLog log, string file, bool explorer)
    {
        // Looked at before the drag source goes away: from Explorer, the app reuses the window the drag started in.
        DropOnWheel(checks, log, file, "action.rename", explorer, [VIRTUAL_KEY.VK_LWIN], afterDrop: label =>
        {
            var done = log.WaitFor(10000, $"queue: {label} ");
            var window = ExplorerSource.FolderWindow(Path.GetDirectoryName(file)!, 5000);
            string? text = null;
            Checks.Wait(() => (text = RenameBox(window)) is not null, 5000);
            checks.Check("重命名 opens Explorer's name box on the file",
                done?.Contains(" done", StringComparison.Ordinal) == true && (text == Path.GetFileName(file) || text == Path.GetFileNameWithoutExtension(file)),
                $"{done ?? "no queue outcome within 10 s"}; {(window.IsNull ? "no Explorer window on the folder" : text is null ? "no name box in its items view" : $"name box holds '{text}'")}");

            // Closing the window ends the edit with the name unchanged.
            ExplorerSource.CloseAndWait(window);
            checks.Check("closing Explorer leaves the name as it was", File.Exists(file), $"exists={File.Exists(file)}");
        });
    }

    /// <summary>
    /// The picker comes up in front of the window the drag came from; the folder is typed into its folder
    /// box and OK pressed. 撤销 in the task window then moves the file back, or recycles the copy.
    /// </summary>
    private static void Transfer(Checks checks, AppLog log, int app, string file, string folder, bool copy, bool explorer)
    {
        var verb = copy ? "复制到" : "移动到";
        var target = Path.Combine(folder, Path.GetFileName(file));
        var how = "no picker";
        var picked = Stopwatch.StartNew();
        if (DropOnWheel(checks, log, file, copy ? "action.copy-to" : "action.move-to", explorer, [VIRTUAL_KEY.VK_LWIN], afterDrop: _ =>
        {
            // Answered while the drag source is still open: that is the window the picker must come up over.
            var dialog = Picker(checks, app, $"把 {Path.GetFileName(file)} {verb}");
            if (!dialog.IsNull)
            {
                how = Choose(dialog, folder);
                picked.Restart();
            }
        }) is not { } label)
        {
            return;
        }

        var done = log.WaitFor(10000, $"queue: {label} ");
        var took = picked.ElapsedMilliseconds;
        var placed = File.Exists(target) && File.Exists(file) == copy;
        checks.Check(copy ? "复制到 copies the file into the picked folder" : "移动到 moves the file into the picked folder",
            done?.Contains(" done", StringComparison.Ordinal) == true && placed,
            $"{how}; {done ?? "no queue outcome within 10 s"}, seen {took} ms after the picker closed; at source={File.Exists(file)}, in folder={File.Exists(target)}");
        if (!placed)
        {
            return;
        }

        var pressed = PressUndo(label, out var undoHow);
        var undone = pressed ? log.WaitFor(10000, $"queue: undid {label}") : null;
        Checks.Wait(() => File.Exists(file) && !File.Exists(target), 3000);
        checks.Check(copy ? "撤销 recycles the copy and keeps the file" : "撤销 moves the file back",
            undone is not null && File.Exists(file) && !File.Exists(target),
            $"{undoHow}; {undone ?? "no 'queue: undid' line within 10 s"}; at source={File.Exists(file)}, in folder={File.Exists(target)}");
    }

    private static void ClosePicker(Checks checks, AppLog log, int app, string file, bool explorer)
    {
        if (DropOnWheel(checks, log, file, "action.move-to", explorer, [VIRTUAL_KEY.VK_LWIN], afterDrop: _ =>
        {
            if (Picker(checks: null, app, "") is { IsNull: false } dialog)
            {
                PInvoke.PostMessage(dialog, PInvoke.WM_CLOSE, new WPARAM(0), new LPARAM(0));
            }
        }) is not { } label)
        {
            return;
        }

        var done = log.WaitFor(10000, $"queue: {label} ");
        checks.Check("closing the folder picker skips the job and leaves the file", done?.Contains("skipped: 没有选文件夹", StringComparison.Ordinal) == true && File.Exists(file),
            $"{done ?? "no queue outcome within 10 s"}; exists={File.Exists(file)}");
    }

    /// <summary>
    /// The app's folder picker, or <paramref name="what"/> else, a visible top-level dialog window of its
    /// process, waited for up to 5 s. With <paramref name="checks"/>, checks that it has the foreground and the
    /// expected title.
    /// </summary>
    private static HWND Picker(Checks? checks, int app, string title, string what = "the folder picker")
    {
        var dialog = default(HWND);
        var watch = Stopwatch.StartNew();
        Checks.Wait(() =>
        {
            for (var w = PInvoke.FindWindowEx(default, default, "#32770", null); !w.IsNull; w = PInvoke.FindWindowEx(default, w, "#32770", null))
            {
                if (OwnerPid(w) == app && PInvoke.IsWindowVisible(w))
                {
                    dialog = w;
                    return true;
                }
            }

            return false;
        }, 5000);
        var appeared = watch.ElapsedMilliseconds;
        var front = !dialog.IsNull && Checks.Wait(() => PInvoke.GetForegroundWindow() == dialog, 2000);
        checks?.Check($"{what} comes up in front, titled '{title}'", front && Title(dialog) == title,
            dialog.IsNull ? "no dialog from the app within 5 s" : $"'{Title(dialog)}' {appeared} ms after the drop; foreground {ForegroundOwner()}");
        return dialog;
    }

    /// <summary>
    /// Types <paramref name="folder"/> into the picker's folder box and presses OK. A typed path can make
    /// the picker open that folder rather than pick it; a second OK then picks the folder it shows.
    /// </summary>
    private static unsafe string Choose(HWND dialog, string folder)
    {
        var box = VisibleDescendant(dialog, "Edit");
        if (box.IsNull)
        {
            PInvoke.PostMessage(dialog, PInvoke.WM_CLOSE, new WPARAM(0), new LPARAM(0));
            return "no folder box in the picker";
        }

        fixed (char* p = folder)
        {
            PInvoke.SendMessage(box, PInvoke.WM_SETTEXT, new WPARAM(0), new LPARAM((nint)p));
        }

        for (var press = 1; press <= 2; press++)
        {
            PInvoke.PostMessage(dialog, PInvoke.WM_COMMAND, new WPARAM(IdOk), new LPARAM(PInvoke.GetDlgItem(dialog, IdOk)));
            if (Checks.Wait(() => !PInvoke.IsWindow(dialog), 1500))
            {
                return $"picked with {press} OK";
            }
        }

        PInvoke.PostMessage(dialog, PInvoke.WM_CLOSE, new WPARAM(0), new LPARAM(0));
        return "the picker stayed open after 2 OK";
    }

    private const int IdOk = 1;

    /// <summary>The text of the in-place name box in the items view of <paramref name="window"/>, or null when there is none.</summary>
    private static unsafe string? RenameBox(HWND window)
    {
        var tab = window.IsNull ? default : PInvoke.FindWindowEx(window, default, "ShellTabWindowClass", null);
        var view = tab.IsNull ? default : PInvoke.FindWindowEx(tab, default, "DUIViewWndClassName", null);
        var box = view.IsNull ? default : VisibleDescendant(view, "Edit");
        if (box.IsNull)
        {
            return null;
        }

        var buffer = new char[512];
        fixed (char* p = buffer)
        {
            var n = (int)PInvoke.SendMessage(box, PInvoke.WM_GETTEXT, new WPARAM((nuint)buffer.Length), new LPARAM((nint)p)).Value;
            return new string(buffer, 0, n);
        }
    }

    private static unsafe HWND VisibleDescendant(HWND parent, string className)
    {
        Span<char> name = stackalloc char[64];
        for (var child = PInvoke.FindWindowEx(parent, default, null, null); !child.IsNull; child = PInvoke.FindWindowEx(parent, child, null, null))
        {
            int n;
            fixed (char* p = name)
            {
                n = PInvoke.GetClassName(child, p, name.Length);
            }

            if (name[..n].SequenceEqual(className) && PInvoke.IsWindowVisible(child))
            {
                return child;
            }

            if (VisibleDescendant(child, className) is { IsNull: false } found)
            {
                return found;
            }
        }

        return default;
    }

    /// <summary>
    /// Whether Start is open, and the process that owns the foreground window. A Win tap that reaches the
    /// shell gives the foreground to a CoreWindow in StartMenuExperienceHost, SearchHost (Windows 11) or
    /// SearchApp (Windows 10). Windows 10 hides those windows from FindWindowEx, so the foreground is what
    /// is checked; both test machines showed this.
    /// </summary>
    private static (bool Open, string Owner) StartMenu()
    {
        var owner = ForegroundOwner();
        return (owner is "StartMenuExperienceHost" or "SearchHost" or "SearchApp", owner);
    }

    /// <summary>The name of the process that owns the foreground window.</summary>
    private static string ForegroundOwner()
    {
        try
        {
            using var process = Process.GetProcessById((int)OwnerPid(PInvoke.GetForegroundWindow()));
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "none";
        }
    }

    /// <summary>The clipboard's text, retried while another process holds the clipboard open.</summary>
    private static string? ClipboardText()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (ExternalException)
            {
                Thread.Sleep(100);
            }
        }

        return null;
    }

    private static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Path.GetFileName(path));
        return path;
    }
}
