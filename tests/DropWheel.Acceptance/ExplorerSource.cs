using System.Diagnostics;
using Windows.Win32.UI.Accessibility;
using ComTreeScope = Windows.Win32.UI.Accessibility.TreeScope;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DropWheel.Acceptance;

/// <summary>
/// Opens a real File Explorer window on the folder containing <c>file</c> and locates the item's
/// screen rectangle through UI Automation so the driver can start a genuine Explorer drag.
/// Verified shape on Win11 22H2 (22621): CabinetWClass → ShellTabWindowClass → DUIViewWndClassName →
/// DUIListView → List "UIItemsView" → ListItem "UIItem" named exactly like the file.  The lookup runs on an
/// STA thread in a hook-free child process through the UIAutomationCore COM client; see <see cref="Open"/>
/// and <see cref="OpenInProcess"/> for what went wrong with everything simpler (copied from DragSpike).
/// </summary>
internal static unsafe class ExplorerSource
{
    /// <summary>Grace period between the items-view HWND appearing and the first UI Automation query.</summary>
    public static int FirstQueryDelayMs = 1000;

    /// <summary>Last Explorer window found, so the caller can close it even if locating the item failed.</summary>
    public static HWND LastWindow;

    /// <summary>
    /// Locates the item from a helper child process (<c>DropWheel.Acceptance --locate</c>) that has no hooks
    /// installed.  Observed on 22621: with the LL/WinEvent hooks up in this process, the same UIA query a
    /// hook-free process answers in ~100 ms returns "view not found" for 15 s (3 runs).  Correlation, not a
    /// proven mechanism; the child process is the cheap way around it.
    /// </summary>
    public static (HWND Window, Rectangle Item) Open(string file, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--locate \"{file}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("could not start locator child");

        string? located = null;
        while (child.StandardOutput.ReadLine() is { } line)
        {
            Log.Info($"locator> {line}");
            if (line.StartsWith("LOCATED ", StringComparison.Ordinal))
            {
                located = line;
            }
        }

        child.WaitForExit((int)timeout.TotalMilliseconds);
        if (located is null)
        {
            throw new TimeoutException($"locator child exited {child.ExitCode} without LOCATED after {sw.ElapsedMilliseconds} ms");
        }

        var p = located.Split(' ');
        LastWindow = new HWND((nint)Convert.ToInt64(p[1], 16));
        var rect = new Rectangle(int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]));
        Log.Info($"explorer: child located item at {rect} in window 0x{(nint)LastWindow.Value:X} after {sw.ElapsedMilliseconds} ms");
        return (LastWindow, rect);
    }

    /// <summary>Entry point of <c>--locate</c>: runs <see cref="OpenInProcess"/> and prints LOCATED / ERROR.</summary>
    public static int LocateMain(string file)
    {
        try
        {
            var (win, r) = OpenInProcess(file, TimeSpan.FromSeconds(20));
            Console.WriteLine($"LOCATED {(nint)win.Value:X} {r.X} {r.Y} {r.Width} {r.Height}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// The actual lookup.  Only works reliably from a process without the LL/WinEvent hooks, and even then
    /// roughly one Explorer window in four never exposes its items view through UIA (both the managed and
    /// the UIAutomationCore client report "not found" for as long as we poll).  A fresh window is fine, so
    /// each attempt gets a short UIA budget and we relaunch Explorer instead of waiting.
    /// </summary>
    public static (HWND Window, Rectangle Item) OpenInProcess(string file, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + timeout;
        Log.Info($"explorer: locating on thread '{Thread.CurrentThread.Name}' apartment={Thread.CurrentThread.GetApartmentState()}");
        for (var attempt = 1; DateTime.UtcNow < deadline; attempt++)
        {
            var budget = DateTime.UtcNow + TimeSpan.FromSeconds(4) < deadline ? TimeSpan.FromSeconds(4) : deadline - DateTime.UtcNow;
            if (Attempt(file, attempt, budget, sw) is { } found)
            {
                return found;
            }
        }

        throw new TimeoutException($"item '{Path.GetFileName(file)}' not found in a fresh Explorer window via UIA within {timeout.TotalSeconds} s");
    }

    private static (HWND Window, Rectangle Item)? Attempt(string file, int attempt, TimeSpan budget, Stopwatch sw)
    {
        var name = Path.GetFileName(file);
        var folderTitle = Path.GetFileName(Path.GetDirectoryName(file)!);
        var deadline = DateTime.UtcNow + budget;
        LastWindow = default;
        Log.Info($"explorer: attempt {attempt}, budget {budget.TotalSeconds:F0} s");

        // Explorer reuses an open window for the same folder instead of creating a new one.
        foreach (var stale in Cabinets().Where(h => IsFolderWindow(h, folderTitle)).ToList())
        {
            Log.Info($"explorer: closing stale window 0x{(nint)stale.Value:X} '{folderTitle}'");
            CloseAndWait(stale);
        }

        KillOrphanExplorers();

        // Must be ShellExecute.  With UseShellExecute=false (CreateProcess) the folder window is hosted by a
        // separate explorer.exe process and the managed UIA client reports an empty/shallow subtree for it
        // forever (15 s of polling, from this process or from a hook-free child), while the same query
        // against a ShellExecute-launched window succeeds in ~100 ms.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true })?.Dispose();
        while (LastWindow.IsNull && DateTime.UtcNow < deadline)
        {
            LastWindow = Cabinets().FirstOrDefault(h => IsFolderWindow(h, folderTitle));
            if (LastWindow.IsNull)
            {
                Thread.Sleep(100);
            }
        }

        if (LastWindow.IsNull)
        {
            Log.Info($"explorer: window titled '{folderTitle}' did not appear within the attempt budget");
            return null;
        }

        Log.Info($"explorer: window 0x{(nint)LastWindow.Value:X} '{folderTitle}' after {sw.ElapsedMilliseconds} ms");

        // Do not touch UIA until Explorer has created the items view HWND, then give it a short grace.
        HWND viewHwnd = default;
        while (viewHwnd.IsNull && DateTime.UtcNow < deadline)
        {
            var tab = PInvoke.FindWindowEx(LastWindow, default, "ShellTabWindowClass", null);
            var dui = tab.IsNull ? default : PInvoke.FindWindowEx(tab, default, "DUIViewWndClassName", null);
            viewHwnd = dui.IsNull ? default : PInvoke.FindWindowEx(dui, default, "DirectUIHWND", null);
            if (viewHwnd.IsNull)
            {
                Thread.Sleep(50);
            }
        }

        Log.Info($"explorer: items view HWND {(viewHwnd.IsNull ? "never appeared" : $"0x{(nint)viewHwnd.Value:X} after {sw.ElapsedMilliseconds} ms")}");
        Thread.Sleep(FirstQueryDelayMs);
        Log.Info($"explorer: first UIA query after {sw.ElapsedMilliseconds} ms");

        // UIAutomationCore COM client (what Inspect/FlaUI use), not the legacy managed System.Windows.Automation
        // client: the managed one reported "view not found" for 15 s on roughly half the runs against a
        // window it had answered in ~100 ms on the others.
        if (DateTime.UtcNow >= deadline)
        {
            return null;
        }

        var uia = (IUIAutomation)new CUIAutomation8();
        var viewCond = uia.CreatePropertyCondition(UIA_PROPERTY_ID.UIA_ClassNamePropertyId, "UIItemsView");
        var itemCond = uia.CreatePropertyCondition(UIA_PROPERTY_ID.UIA_NamePropertyId, name);

        // With "hide extensions for known file types" on, the item is named without its extension. Which
        // types count as known varies (here .jpg kept its extension, .png and .srt lost theirs).
        var stemCond = uia.CreatePropertyCondition(UIA_PROPERTY_ID.UIA_NamePropertyId, Path.GetFileNameWithoutExtension(file));
        while (DateTime.UtcNow < deadline)
        {
            var t0 = sw.ElapsedMilliseconds;
            var root = uia.ElementFromHandle(LastWindow);
            var view = root.FindFirst(ComTreeScope.TreeScope_Descendants, viewCond);
            var t1 = sw.ElapsedMilliseconds;
            var item = view?.FindFirst(ComTreeScope.TreeScope_Children, itemCond) ?? view?.FindFirst(ComTreeScope.TreeScope_Children, stemCond);
            var t2 = sw.ElapsedMilliseconds;
            Log.Info($"explorer: UIA view {(view is null ? "not found" : "found")} in {t1 - t0} ms, item {(item is null ? "not found" : "found")} in {t2 - t1} ms");
            if (item is null && view is not null && sw.ElapsedMilliseconds > 4000)
            {
                var kids = view.FindAll(ComTreeScope.TreeScope_Children, uia.CreateTrueCondition());
                var names = new List<string>();
                for (var i = 0; i < kids.Length; i++)
                {
                    var k = kids.GetElement(i);
                    names.Add($"{k.CurrentControlType}:'{k.CurrentName}'/{k.CurrentClassName}");
                }

                Log.Info($"explorer: view '{view.CurrentName}' rect {view.CurrentBoundingRectangle} has {kids.Length} children: {string.Join(" | ", names)}");
            }

            if (item is not null)
            {
                var r = item.CurrentBoundingRectangle;
                if (r.Width > 0)
                {
                    var rect = new Rectangle(r.X, r.Y, r.Width, r.Height);
                    Log.Info($"explorer: item '{item.CurrentName}' (control type {item.CurrentControlType}, class {item.CurrentClassName}) at {rect} after {sw.ElapsedMilliseconds} ms");
                    return (LastWindow, rect);
                }
            }

            Thread.Sleep(250);
        }

        Log.Info($"explorer: attempt {attempt} gave up on window 0x{(nint)LastWindow.Value:X}; closing it");
        CloseAndWait(LastWindow);
        return null;
    }

    public static void Close(HWND window)
    {
        if (!window.IsNull)
        {
            PInvoke.PostMessage(window, PInvoke.WM_CLOSE, new WPARAM(0), new LPARAM(0));
        }
    }

    /// <summary>
    /// Closes the window and waits (up to 2 s) for it to go.  A window that ignores WM_CLOSE is hosted by a
    /// wedged explorer.exe (observed: the same HWND came back run after run, never answering UIA either),
    /// so its process is killed, unless it is the shell itself.
    /// </summary>
    public static void CloseAndWait(HWND window)
    {
        if (window.IsNull)
        {
            return;
        }

        Close(window);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Cabinets().Contains(window) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        if (!Cabinets().Contains(window))
        {
            return;
        }

        uint pid = 0;
        _ = PInvoke.GetWindowThreadProcessId(window, &pid);
        if (pid == 0 || pid == ShellPid())
        {
            Log.Info($"explorer: window 0x{(nint)window.Value:X} ignored WM_CLOSE and belongs to the shell (pid {pid}); leaving it");
            return;
        }

        try
        {
            using var p = Process.GetProcessById((int)pid);
            p.Kill();
            p.WaitForExit(2000);
            Log.Info($"explorer: window 0x{(nint)window.Value:X} ignored WM_CLOSE; killed its explorer.exe {pid}");
        }
        catch (Exception ex)
        {
            Log.Info($"explorer: could not kill explorer.exe {pid}: {ex.Message}");
        }
    }

    private static uint ShellPid()
    {
        uint shellPid = 0;
        var tray = PInvoke.FindWindow("Shell_TrayWnd", null);
        if (!tray.IsNull)
        {
            _ = PInvoke.GetWindowThreadProcessId(tray, &shellPid);
        }

        return shellPid;
    }

    /// <summary>
    /// Every <c>explorer.exe /select</c> on this build leaves an explorer.exe process behind after its window
    /// closes, and a new /select can be routed to one that is on its way out (observed: window never appears).
    /// Kill explorer processes that are neither the shell (owner of Shell_TrayWnd) nor hosting a folder window.
    /// </summary>
    private static void KillOrphanExplorers()
    {
        var shellPid = ShellPid();
        var hosting = new HashSet<uint>();
        foreach (var w in Cabinets())
        {
            uint pid = 0;
            _ = PInvoke.GetWindowThreadProcessId(w, &pid);
            hosting.Add(pid);
        }

        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            using (p)
            {
                if ((uint)p.Id == shellPid || hosting.Contains((uint)p.Id))
                {
                    continue;
                }

                try
                {
                    p.Kill();
                    p.WaitForExit(2000);
                    Log.Info($"explorer: killed orphan explorer.exe {p.Id}");
                }
                catch (Exception ex)
                {
                    Log.Info($"explorer: could not kill explorer.exe {p.Id}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// The title is the folder name on some builds and "folder - File Explorer" (localized) on others; the
    /// same 22H2 VM switched from one to the other after a cumulative update.
    /// </summary>
    private static bool IsFolderWindow(HWND h, string folder)
    {
        var text = WindowText(h);
        return text == folder || text.StartsWith(folder + " - ", StringComparison.Ordinal);
    }

    private static string WindowText(HWND h)
    {
        Span<char> buf = stackalloc char[512];
        var n = PInvoke.GetWindowText(h, buf);
        return n > 0 ? new string(buf[..n]) : "";
    }

    private static IEnumerable<HWND> Cabinets()
    {
        var h = new HWND(0);
        while (true)
        {
            h = PInvoke.FindWindowEx(new HWND(0), h, "CabinetWClass", null);
            if (h.IsNull)
            {
                yield break;
            }

            yield return h;
        }
    }
}
