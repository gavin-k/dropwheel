using System.Collections.Concurrent;
using DropWheel.Core.Input;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using DropWheel.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropWheel.Acceptance;

/// <summary>
/// <see cref="DragMonitor"/> with the default key bindings during a real drag: phases, the three
/// default taps, a Win tap that must not open Start, and a long Shift hold that must not count.
/// The drag is cancelled with Esc, so nothing is dropped anywhere.
/// </summary>
internal static class HooksScenario
{
    public static int Run(bool explorer)
    {
        var checks = new Checks();
        var file = HostScenario.SampleFile("hooks-drag.jpg");
        var phases = new ConcurrentQueue<(DragPhase Phase, double At)>();
        var invoked = new ConcurrentQueue<(WheelMode Mode, double At)>();
        var faults = new ConcurrentQueue<Exception>();

        var tapper = new ModifierTapper(KeyBindings.Default, TimeProvider.System);
        tapper.Invoked += m => invoked.Enqueue((m, Log.NowMs));
        using var monitor = new DragMonitor(tapper, new WinKeyGate(KeyBindings.Default), new TypeAhead());
        monitor.PhaseChanged += p => phases.Enqueue((p, Log.NowMs));
        monitor.Faulted += faults.Enqueue;
        monitor.Start();

        try
        {
            using var source = DragSource.Open(file, explorer);
            var t0 = Log.NowMs;
            var mid = source.PressAndDrag();
            var expected = explorer ? DragPhase.Dragging : DragPhase.MaybeDragging;
            Checks.Wait(() => monitor.Phase == expected, 1000);
            checks.Check($"drag reaches {expected}", monitor.Phase == expected,
                string.Join(", ", phases.Select(p => $"{p.Phase} +{p.At - t0:F0} ms")));
            checks.Check("last point follows the mouse", Near(monitor.LastPoint, mid), $"{monitor.LastPoint} vs {mid}");

            Tap(checks, invoked, "Shift tap opens Convert", WheelMode.Convert, VIRTUAL_KEY.VK_LSHIFT);
            Tap(checks, invoked, "Ctrl+Alt tap opens Tools", WheelMode.Tools, VIRTUAL_KEY.VK_LCONTROL, VIRTUAL_KEY.VK_LMENU);

            // Win10 21H2 opens a "Search" window that StartMenuVisible does not see (DragSpike finding 9),
            // so the foreground window not changing is the second half of the check.
            var startBefore = StartMenuVisible();
            var fgBefore = PInvoke.GetForegroundWindow();
            Tap(checks, invoked, "Win tap opens Actions", WheelMode.Actions, VIRTUAL_KEY.VK_LWIN);
            Thread.Sleep(600);
            var startAfter = StartMenuVisible();
            var fgAfter = PInvoke.GetForegroundWindow();
            checks.Check("Win tap did not open Start or Search", !startAfter && fgAfter == fgBefore,
                $"Start visible before={startBefore} after={startAfter}; foreground \"{Title(fgBefore)}\" -> \"{Title(fgAfter)}\"");

            invoked.Clear();
            Input.Key(VIRTUAL_KEY.VK_LSHIFT, up: false);
            Thread.Sleep(500);
            Input.Key(VIRTUAL_KEY.VK_LSHIFT, up: true);
            Thread.Sleep(300);
            checks.Check("Shift held 500 ms is not a tap", invoked.IsEmpty, $"invoked [{string.Join(", ", invoked.Select(i => i.Mode))}]");

            Input.Key(VIRTUAL_KEY.VK_ESCAPE, up: false);
            Input.Key(VIRTUAL_KEY.VK_ESCAPE, up: true);
            Thread.Sleep(300);
            var tUp = Log.NowMs;
            Input.LeftUp();
            var idle = Checks.Wait(() => monitor.Phase == DragPhase.Idle, 1000);
            checks.Check("left button up returns to Idle", idle, idle ? $"{phases.Last().At - tUp:F0} ms after button up" : $"still {monitor.Phase}");
            if (!explorer)
            {
                var result = source.Result(TimeSpan.FromSeconds(5));
                checks.Check("Esc cancelled the drag", result?.Contains("None", StringComparison.Ordinal) == true, result ?? "no RESULT");
            }

            checks.Check("source file still in place", File.Exists(file), file);
        }
        catch (Exception ex)
        {
            checks.Check("no exception", false, ex.ToString());
        }
        finally
        {
            foreach (var key in new[] { VIRTUAL_KEY.VK_LSHIFT, VIRTUAL_KEY.VK_LCONTROL, VIRTUAL_KEY.VK_LMENU })
            {
                Input.Key(key, up: true);
            }

            if (StartMenuVisible())
            {
                Input.Key(VIRTUAL_KEY.VK_ESCAPE, up: false);
                Input.Key(VIRTUAL_KEY.VK_ESCAPE, up: true);
            }
        }

        checks.Check("no hook faults", faults.IsEmpty, string.Join("; ", faults.Select(f => f.Message)));
        return checks.Report($"DragMonitor ({(explorer ? "Explorer" : "WinForms")} source)");
    }

    private static void Tap(Checks checks, ConcurrentQueue<(WheelMode Mode, double At)> invoked, string name, WheelMode expected, params VIRTUAL_KEY[] keys)
    {
        invoked.Clear();
        foreach (var key in keys)
        {
            Input.Key(key, up: false);
            Thread.Sleep(30);
        }

        Thread.Sleep(40);
        double tUp = 0;
        foreach (var key in keys)
        {
            tUp = Log.NowMs;
            Input.Key(key, up: true);
            Thread.Sleep(20);
        }

        var got = Checks.Wait(() => !invoked.IsEmpty, 1000);
        Thread.Sleep(100);
        var modes = invoked.Select(i => i.Mode).ToList();
        checks.Check(name, got && modes is [var only] && only == expected,
            got ? $"[{string.Join(", ", modes)}] {invoked.First().At - tUp:F1} ms after the last key up" : "nothing within 1 s");
    }

    private static string Title(HWND h)
    {
        Span<char> buf = stackalloc char[128];
        var n = PInvoke.GetWindowText(h, buf);
        return new string(buf[..n]);
    }

    private static bool Near((int X, int Y) a, Point b) => Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2;

    /// <summary>
    /// Start is a CoreWindow titled "Start" (localized). It stays WS_VISIBLE and is DWM-cloaked while
    /// closed, so "open" means visible and not cloaked.
    /// </summary>
    private static unsafe bool StartMenuVisible()
    {
        var h = default(HWND);
        Span<char> buf = stackalloc char[64];
        while (true)
        {
            h = PInvoke.FindWindowEx(default, h, "Windows.UI.Core.CoreWindow", null);
            if (h.IsNull)
            {
                return false;
            }

            var n = PInvoke.GetWindowText(h, buf);
            if (new string(buf[..n]) is "Start" or "开始" or "開始" && PInvoke.IsWindowVisible(h))
            {
                var cloaked = 0;
                _ = PInvoke.DwmGetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
                if (cloaked == 0)
                {
                    return true;
                }
            }
        }
    }
}
