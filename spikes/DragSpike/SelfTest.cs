using System.Diagnostics;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DragSpike;

/// <summary>
/// Drives the whole chain with SendInput: start a drag (from our own source window, or from a real
/// Explorer window with <c>--explorer</c>), tap Shift mid-drag, expect the wheel, move onto sector 2,
/// release, expect the drop.  Prints a PASS/FAIL table and returns a process exit code.
/// </summary>
internal sealed class SelfTest(HookThread hooks, WheelForm wheel, bool explorer)
{
    private const int TargetSector = 2;
    private const int DragDistancePx = 520;

    private readonly ManualResetEventSlim _tapped = new();
    private readonly ManualResetEventSlim _dropped = new();
    private double _tTapHook;
    private IReadOnlyList<string>? _droppedPaths;
    private int _droppedSector = -1;

    public int Run()
    {
        var results = new List<(string Name, bool Ok, string Detail)>();
        void Check(string name, bool ok, string detail) { results.Add((name, ok, detail)); Log.Info($"{(ok ? "PASS" : "FAIL")}  {name}: {detail}"); }

        var dir = Path.Combine(Path.GetTempPath(), "DropWheelSpike");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "sample-drop.jpg");
        File.WriteAllBytes(file, new byte[1024]);

        hooks.ShiftTapped += (_, t) => { _tTapHook = t; _tapped.Set(); };
        wheel.Dropped += (paths, s) => { _droppedPaths = paths; _droppedSector = s; _dropped.Set(); };

        Process? source = null;
        HWND explorerWindow = default;
        var sourceOut = new List<string>();
        try
        {
            // ---- 1. a drag source --------------------------------------------------------------
            Point start;
            if (explorer)
            {
                var (win, item) = ExplorerSource.Open(file, TimeSpan.FromSeconds(25));
                explorerWindow = win;
                start = new Point(item.X + item.Width / 2, item.Y + item.Height / 2);
            }
            else
            {
                var vs = SystemInformation.VirtualScreen;
                var at = new Point(vs.X + 120, vs.Y + 160);
                source = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--dragsource \"{file}\" --at {at.X},{at.Y}")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                });
                ArgumentNullException.ThrowIfNull(source);
                var ready = ReadLineWithPrefix(source, sourceOut, "READY", TimeSpan.FromSeconds(10))
                    ?? throw new TimeoutException("drag source never printed READY");
                var p = ready.Split(' ');
                var bounds = new Rectangle(int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4]));
                start = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2 + 20);
                Log.Info($"driver: source window {bounds}");
            }

            // ---- 2. press and drag -------------------------------------------------------------
            Thread.Sleep(400);
            Input.MoveTo(start.X, start.Y);
            Thread.Sleep(150);
            Input.LeftDown();
            Thread.Sleep(120);
            var tDragSend = Log.NowMs;
            var mid = new Point(start.X + DragDistancePx, start.Y);
            Input.MoveSmooth(start, mid, 14, 25);
            Thread.Sleep(250);

            var dragging = hooks.Dragging;
            Check("drag detected", dragging, $"{hooks.DragSignals}; winevent {Fmt(hooks.TWinEventStart, tDragSend)}, sysdragimage {Fmt(hooks.TSysDragImage, tDragSend)}, llmouse {Fmt(hooks.TMoveThreshold, tDragSend)} after first move");

            // ---- 3. tap Shift -------------------------------------------------------------------
            _tapped.Reset();
            Input.Key(VIRTUAL_KEY.VK_LSHIFT, up: false);
            Thread.Sleep(70);
            var tSendUp = Log.NowMs;
            Input.Key(VIRTUAL_KEY.VK_LSHIFT, up: true);
            var tapped = _tapped.Wait(1500);
            Check("Shift tap seen by LL hook during drag", tapped, tapped ? $"hook latency {_tTapHook - tSendUp:F1} ms after SendInput key-up" : "no ShiftTapped within 1.5 s");

            var shown = SpinUntil(() => wheel.TShown.HasValue, 1500);
            var showMs = shown ? wheel.TShown!.Value - _tTapHook : double.NaN;
            Check("wheel visible within 150 ms budget", shown && showMs <= 150, shown ? $"first paint {showMs:F1} ms after tap" : "wheel never painted");

            // ---- 4. move onto sector 2 and release --------------------------------------------
            var target = wheel.PointForSector(TargetSector);
            Input.MoveSmooth(mid, target, 8, 30);
            Thread.Sleep(200);
            var dragEndedEarly = hooks.TWinEventStart is { } s0 && hooks.TWinEventEnd is { } e0 && e0 > s0;
            Check("drag survived the wheel appearing", !dragEndedEarly, dragEndedEarly ? "WinEvent DRAGDROPEND fired before the drop" : hooks.TWinEventStart.HasValue ? "no DRAGDROPEND before drop" : "n/a (no WinEvent for this source)");

            var tDropSend = Log.NowMs;
            Input.LeftUp();
            var dropped = _dropped.Wait(3000);
            var pathOk = dropped && _droppedPaths is [var only] && string.Equals(only, file, StringComparison.OrdinalIgnoreCase);
            Check("drop received with the right file", pathOk, dropped ? $"[{string.Join(", ", _droppedPaths!)}] {Log.NowMs - tDropSend:F0} ms after button up" : "no DragDrop within 3 s");
            Check("drop landed in the targeted sector", dropped && _droppedSector == TargetSector, $"sector={_droppedSector} expected={TargetSector}");

            // ---- 5. the source's view -----------------------------------------------------------
            if (source is not null)
            {
                var result = ReadLineWithPrefix(source, sourceOut, "RESULT", TimeSpan.FromSeconds(5));
                Check("source DoDragDrop returned Copy", result is not null && result.Contains("Copy"), result ?? "no RESULT line");
            }
        }
        catch (Exception ex)
        {
            Check("no exception", false, ex.ToString());
        }
        finally
        {
            Input.LeftUp();
            Input.Key(VIRTUAL_KEY.VK_LSHIFT, up: true);
            try { if (source is { HasExited: false }) { source.Kill(); } } catch { }
            ExplorerSource.CloseAndWait(explorerWindow.IsNull ? ExplorerSource.LastWindow : explorerWindow);
            wheel.BeginInvoke(wheel.Hide);
        }

        Log.Info("");
        Log.Info($"==== self-test ({(explorer ? "Explorer" : "WinForms")} source) ====");
        foreach (var (name, ok, detail) in results)
        {
            Log.Info($"  {(ok ? "✅" : "❌")} {name}  —  {detail}");
        }

        var passed = results.All(r => r.Ok);
        Log.Info(passed ? "RESULT: PASS" : "RESULT: FAIL");
        return passed ? 0 : 1;
    }

    private static string Fmt(double? t, double since) => t is { } v ? $"+{v - since:F0} ms" : "none";

    private static bool SpinUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return cond();
    }

    private static string? ReadLineWithPrefix(Process p, List<string> sink, string prefix, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var task = p.StandardOutput.ReadLineAsync();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !task.Wait(remaining))
            {
                return null;
            }

            var line = task.Result;
            if (line is null)
            {
                return null;
            }

            sink.Add(line);
            Log.Info($"source> {line}");
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line;
            }
        }

        return null;
    }
}
