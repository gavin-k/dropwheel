using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using DropWheel.Core.Wheel;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    private const string RecipeId = "user.web-jpg";

    /// <summary>
    /// Wheels after the user-tools scenario, when the image Tools wheel has a group and so a ring-sized footprint:
    /// a second modifier tap while a wheel is open switches wheels and keeps the drag (M1.5 ②); the app says
    /// whether motion is on; near the top edge the ring-sized wheel is pulled down far enough for the whole ring.
    /// </summary>
    private static void SwitchWheels(Checks checks, AppLog log, string jpg, bool explorer)
    {
        using (var drag = DragSource.Open(jpg, explorer))
        {
            var mid = drag.PressAndDrag();
            log.Mark();
            if (Tap(checks, log, mid) is not { } w)
            {
                return;
            }

            var motion = log.WaitFor(1000, "overlay: motion");
            checks.Check("the overlay says whether the wheel is animated", motion is not null, motion ?? "no 'overlay: motion' line within 1 s");

            var entered = log.WaitFor(2000, "file(s) entered");
            log.Mark();
            PressAndRelease(CtrlAlt);
            var tools = log.WaitFor(2000, "wheel: switched to Tools");
            var group = SlotOf(tools, "group:示例");
            checks.Check("Ctrl+Alt on the open Convert wheel switches to the Tools wheel with the file still in hand",
                tools is not null && group >= 0, tools ?? $"no 'wheel: switched to Tools' line within 2 s; {entered ?? "no 'file(s) entered' line"}");
            var again = log.Find("wheel: shown");
            checks.Check("switching does not open a second wheel", again is null, again ?? "none");

            log.Mark();
            PressAndRelease(VIRTUAL_KEY.VK_LSHIFT);
            var back = log.WaitFor(2000, "wheel: switched to Convert");
            checks.Check("Shift switches back to the Convert wheel", back is not null && SlotOf(back, "png") >= 0, back ?? "no 'wheel: switched to Convert' line within 2 s");

            Input.MoveSmooth(new Point(mid.X + 6, mid.Y + 6), new Point(w.X, w.Y), 6, 30);
            Thread.Sleep(150);
            Input.LeftUp();
            var closed = log.WaitFor(2000, "wheel: closed");
            checks.Check("a release on the hub after switching runs nothing", closed is not null && log.Find("wheel: enqueued") is null, closed ?? "wheel never closed");
        }

        // The Tools wheel has a group now, so it opens with room for the ring: near the top edge that is the whole ring, not just the disc.
        using (var drag = DragSource.Open(jpg, explorer))
        {
            var mid = drag.PressAndDrag();
            var edge = NearTopEdge(mid);
            Input.MoveSmooth(mid, edge, 8, 30);
            Thread.Sleep(250);
            log.Mark();
            if (Tap(checks, log, edge, CtrlAlt) is not { } w)
            {
                return;
            }

            var top = Screen.FromPoint(edge).WorkingArea.Top;
            var ringTop = w.Y - (WheelGeometry.RingOuterRadius + WheelGeometry.RingMargin) * w.Scale;
            checks.Check("near the top edge a wheel with a group is pulled down so the whole ring fits in the work area",
                ringTop >= top - 1, $"centre {w.X},{w.Y} scale {w.Scale}: ring top {ringTop:F0}, work area top {top}");
            Input.MoveSmooth(new Point(edge.X + 6, edge.Y + 6), new Point(w.X, w.Y), 6, 30);
            Thread.Sleep(150);
            Input.LeftUp();
            log.WaitFor(2000, "wheel: closed");
        }
    }

    /// <summary>
    /// The shipped recipe example (M1.5 ④): it validates from the command line, is loaded and placed in the 示例
    /// group when copied into the tools folder, needs no trust switch, and run on a PNG through the ring leaves a
    /// half-size JPG named by the steps' own suffixes next to the source, with no scratch folder behind.
    /// </summary>
    private static void Recipe(Checks checks, AppLog log, string exe, string profile, bool explorer)
    {
        var examples = Path.Combine(Path.GetDirectoryName(exe)!, "skills", "dropwheel-tool", "examples");
        var tools = Path.Combine(profile, "tools");
        var dir = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "usertools");
        Directory.CreateDirectory(dir);
        var recipe = Path.Combine(examples, "recipe-web-jpg");
        var (code, json) = ToolCommand(exe, "--validate-tool", recipe);
        checks.Check("DropWheel.exe --validate-tool accepts the shipped recipe example", code == 0 && Problems(json) == "", $"exit {code}; {Shorten(json)}");

        log.Mark();
        CopyTree(recipe, Path.Combine(tools, "recipe-web-jpg"));
        var loaded = log.WaitFor(5000, " loaded", RecipeId);
        var placed = loaded is null ? null : log.WaitFor(2000, $"layout: {RecipeId} placed");
        checks.Check("the recipe is loaded and placed while the app runs", loaded is not null && placed is not null, $"{loaded ?? "no 'loaded' line within 5 s"}; {placed ?? "no 'layout: placed' line"}");
        if (loaded is null)
        {
            return;
        }

        var png = Path.Combine(dir, $"rc-{DateTime.Now:HHmmss}.png");
        WritePng(png, 640, 400);
        using var ring = OpenRing(checks, log, png, explorer, "with the recipe");
        var child = ring is null ? -1 : ChildIndex(ring, RecipeId);
        checks.Check("the recipe is live in the ring without a trust switch", ring is not null && child >= 0 && !IsOff(ring, RecipeId), ring?.Line ?? "no ring");
        if (ring is null)
        {
            return;
        }

        if (child < 0)
        {
            Input.LeftUp();
            log.WaitFor(3000, "wheel: closed");
            return;
        }

        log.Mark();
        Input.MoveSmooth(SectorPoint(ring.Wheel, ring.Parent), ChildPoint(ring, child), 6, 30);
        Thread.Sleep(250);
        Input.LeftUp();
        var enqueued = log.WaitFor(3000, "wheel: enqueued ", "发网上");
        var label = enqueued is null ? null : enqueued[(enqueued.IndexOf("wheel: enqueued ", StringComparison.Ordinal) + "wheel: enqueued ".Length)..];
        var done = label is null ? null : log.WaitFor(60000, $"queue: {label} ");
        var output = done is null ? null : OutputsRegex().Match(done) is { Success: true } m ? m.Groups[1].Value : null;
        var stem = Path.GetFileNameWithoutExtension(png);
        var named = output is not null && Regex.IsMatch(Path.GetFileName(output), $"^{Regex.Escape(stem)}-320x200(-压缩)?\\.jpg$", RegexOptions.IgnoreCase);
        var scratch = Directory.GetFileSystemEntries(dir, "*.dropwheel-tmp");
        checks.Check("发网上 through the ring writes a 320x200 JPG named by its steps next to the source and leaves no scratch folder",
            named && File.Exists(output!) && Path.GetDirectoryName(output) == dir && scratch.Length == 0,
            $"{enqueued ?? "nothing enqueued within 3 s"}; {done ?? "no queue outcome within 60 s"}; scratch [{string.Join(", ", scratch.Select(f => Path.GetFileName(f)))}]");
    }

    /// <summary>
    /// A user tool's process goes with the app even when the app dies without a chance to clean up: a fresh
    /// instance on the profile the main run left (sleeper trusted and in the 示例 group) runs the sleeper and is
    /// killed; the Job Object takes its pwsh with it.
    /// </summary>
    private static void HardKill(Checks checks, string exe, bool explorer)
    {
        var profile = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "profile");
        var dir = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "usertools");
        Directory.CreateDirectory(dir);
        var jpg = Path.Combine(dir, $"hk-{DateTime.Now:HHmmss}.jpg");
        WriteJpeg(jpg, 640, 400);
        var log = new AppLog();
        using var app = Process.Start(new ProcessStartInfo(exe, ["--data-dir", profile]) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })
            ?? throw new InvalidOperationException($"could not start {exe}");
        try
        {
            if (log.WaitFor(30000, "ready:") is null)
            {
                checks.Check("a second instance for the hard-kill check starts", false, "no 'ready:' line within 30 s");
                return;
            }

            var before = Process.GetProcessesByName("pwsh").Select(p => p.Id).ToHashSet();
            var children = new List<int>();
            using (var ring = OpenRing(checks, log, jpg, explorer, "for the hard-kill check"))
            {
                var slow = ring is null ? -1 : ChildIndex(ring, SleeperId);
                if (ring is null)
                {
                    return;
                }

                if (slow < 0)
                {
                    checks.Check("the sleeper is still in the ring on the kept profile", false, ring.Line);
                    Input.LeftUp();
                    return;
                }

                log.Mark();
                Input.MoveSmooth(SectorPoint(ring.Wheel, ring.Parent), ChildPoint(ring, slow), 6, 30);
                Thread.Sleep(250);
                Input.LeftUp();
                var enqueued = log.WaitFor(3000, "wheel: enqueued ", "慢工具");
                var started = Checks.Wait(() => Process.GetProcessesByName("pwsh").Any(p => !before.Contains(p.Id)), 15000);
                children.AddRange(Process.GetProcessesByName("pwsh").Select(p => p.Id).Where(id => !before.Contains(id)));
                if (!checks.Check("the sleeper's pwsh is running before the app is killed", enqueued is not null && started, $"{enqueued ?? "nothing enqueued"}; new pwsh pids [{string.Join(", ", children)}]"))
                {
                    return;
                }
            }

            app.Kill();
            app.WaitForExit(5000);
            var gone = Checks.Wait(() => children.All(id => !Process.GetProcesses().Any(p => p.Id == id)), 5000);
            checks.Check("a user tool's pwsh is ended when the app is killed outright", gone, $"pwsh pids [{string.Join(", ", children)}] {(gone ? "gone" : "still running 5 s after the kill")}");
        }
        finally
        {
            if (!app.HasExited)
            {
                app.Kill();
            }
        }
    }

    private static void WritePng(string path, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        bitmap.SetResolution(96, 96);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.SteelBlue);
            g.FillEllipse(Brushes.Orange, width / 3, height / 4, width / 3, height / 2);
        }

        bitmap.Save(path, ImageFormat.Png);
    }
}
