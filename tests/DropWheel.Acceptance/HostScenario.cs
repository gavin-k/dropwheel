using System.Collections.Concurrent;
using DropWheel.Core.Wheel;
using DropWheel.Shell;

namespace DropWheel.Acceptance;

/// <summary>
/// ADR-0004 risk 1: does the alpha-1 full-screen <see cref="DropTargetHost"/> receive a real OLE drag?
/// No wheel, no app: the host is shown in this process at the drag position and the mouse is moved
/// onto a sector, off the wheel, and onto the centre.
/// </summary>
internal static class HostScenario
{
    private const int TargetSector = 2;

    public static int Run(bool explorer)
    {
        var checks = new Checks();
        var file = SampleFile("host-drop.jpg");
        var events = new ConcurrentQueue<string>();
        IReadOnlyList<string>? entered = null;
        IReadOnlyList<string>? dropped = null;
        var droppedSector = -1;
        var hovered = -1;
        var canceled = 0;

        using var host = new DropTargetHost();
        host.Start();
        host.Entered += p => { entered = p; events.Enqueue($"entered {p.Count}"); };
        host.Hovered += s => { hovered = s; events.Enqueue($"hover {s}"); };
        host.Dropped += (p, s) => { dropped = p; droppedSector = s; events.Enqueue($"drop {s}"); };
        host.Canceled += () => { Interlocked.Increment(ref canceled); events.Enqueue("cancel"); };

        void Reset()
        {
            entered = null;
            dropped = null;
            droppedSector = -1;
            hovered = -1;
            canceled = 0;
            events.Clear();
        }

        // 1. Release on an enabled sector → Dropped with the file and the sector, source sees Copy.
        Attempt(checks, "drop on sector", () =>
        {
            using var source = DragSource.Open(file, explorer);
            var mid = source.PressAndDrag();
            var scale = DragSource.ScaleAt(mid);
            host.Show(new WheelPlacement(mid.X, mid.Y, scale, [true, true, true, true, true, true]));
            var (cx, cy) = WheelGeometry.SectorCenter(TargetSector);
            var target = new Point(mid.X + (int)(cx * scale), mid.Y + (int)(cy * scale));
            Input.MoveSmooth(mid, target, 8, 30);
            Thread.Sleep(200);
            checks.Check("host got DragEnter with the file", entered is [var e] && Same(e, file), $"entered=[{string.Join(", ", entered ?? [])}] (scale {scale:F2})");
            checks.Check("hovered sector tracked", hovered == TargetSector, $"hovered={hovered}");
            var t0 = Log.NowMs;
            Input.LeftUp();
            var got = Checks.Wait(() => dropped is not null, 3000);
            checks.Check("drop delivered to the sector", got && droppedSector == TargetSector && dropped is [var d] && Same(d, file),
                got ? $"sector {droppedSector}, [{string.Join(", ", dropped!)}], {Log.NowMs - t0:F0} ms after button up" : "no Dropped within 3 s");
            host.Hide();
            Thread.Sleep(300);
            checks.Check("source file still in place", File.Exists(file), file);
            if (!explorer)
            {
                var result = source.Result(TimeSpan.FromSeconds(5));
                checks.Check("source saw Copy (never Move)", result?.Contains("Copy", StringComparison.Ordinal) == true, result ?? "no RESULT");
            }
        });

        // 2. Release off the wheel → Canceled, nothing dropped, source sees None.
        Reset();
        Attempt(checks, "release off the wheel", () => ReleaseAway(checks, host, file, explorer, () => (0, 230), "off the wheel", () => dropped is null && canceled == 1));

        // 3. Release on the centre (the hole) → same.
        Reset();
        Attempt(checks, "release on the centre", () => ReleaseAway(checks, host, file, explorer, () => (0, 0), "on the centre", () => dropped is null && canceled == 1));

        // 4. Disabled sector → no drop.
        Reset();
        Attempt(checks, "release on a disabled sector", () =>
        {
            using var source = DragSource.Open(file, explorer);
            var mid = source.PressAndDrag();
            var scale = DragSource.ScaleAt(mid);
            host.Show(new WheelPlacement(mid.X, mid.Y, scale, [true, true, false, true, true, true]));
            var (cx, cy) = WheelGeometry.SectorCenter(TargetSector);
            Input.MoveSmooth(mid, new Point(mid.X + (int)(cx * scale), mid.Y + (int)(cy * scale)), 8, 30);
            Thread.Sleep(200);
            Input.LeftUp();
            Checks.Wait(() => canceled > 0 || dropped is not null, 3000);
            checks.Check("disabled sector does not take the drop", dropped is null && canceled == 1, $"dropped={(dropped is null ? "no" : "yes")} canceled={canceled}");
            host.Hide();
        });

        Log.Info($"host events: {string.Join(" | ", events)}");
        return checks.Report($"DropTargetHost ({(explorer ? "Explorer" : "WinForms")} source)");
    }

    private static void ReleaseAway(Checks checks, DropTargetHost host, string file, bool explorer, Func<(int Dx, int Dy)> offset, string where, Func<bool> ok)
    {
        using var source = DragSource.Open(file, explorer);
        var mid = source.PressAndDrag();
        var scale = DragSource.ScaleAt(mid);
        host.Show(new WheelPlacement(mid.X, mid.Y, scale, [true, true, true, true, true, true]));
        var (dx, dy) = offset();
        var at = new Point(mid.X + (int)(dx * scale), mid.Y + (int)(dy * scale));
        // Pass over a sector first so the host has seen the drag before it leaves the wheel.
        var (sx, sy) = WheelGeometry.SectorCenter(TargetSector);
        var over = new Point(mid.X + (int)(sx * scale), mid.Y + (int)(sy * scale));
        Input.MoveSmooth(mid, over, 6, 30);
        Input.MoveSmooth(over, at, 6, 30);
        Thread.Sleep(200);
        Input.LeftUp();
        Checks.Wait(ok, 3000);
        checks.Check($"release {where} is a cancel", ok(), "expected no drop and one cancel");
        host.Hide();
        if (!explorer)
        {
            var result = source.Result(TimeSpan.FromSeconds(5));
            checks.Check($"source saw None after release {where}", result?.Contains("None", StringComparison.Ordinal) == true, result ?? "no RESULT");
        }
    }

    private static void Attempt(Checks checks, string name, Action scenario)
    {
        Log.Info($"---- {name} ----");
        try
        {
            scenario();
        }
        catch (Exception ex)
        {
            checks.Check($"{name}: no exception", false, ex.ToString());
        }
    }

    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    public static string SampleFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name);
        File.WriteAllBytes(file, new byte[1024]);
        return file;
    }
}
