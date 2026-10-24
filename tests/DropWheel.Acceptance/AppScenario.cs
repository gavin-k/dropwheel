using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.RegularExpressions;
using DropWheel.Core.Wheel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using ComTreeScope = Windows.Win32.UI.Accessibility.TreeScope;

namespace DropWheel.Acceptance;

/// <summary>
/// The M0 acceptance flow against the real app: drag a JPG, tap Shift, release on the PNG sector, expect
/// a PNG next to the JPG; press Undo in the task window, expect the PNG in the Recycle Bin. Then a second
/// drag released off the wheel must convert nothing. The app is started by path and observed only through
/// its log file and UI Automation, so this project never references DropWheel.App.
/// </summary>
internal static partial class AppScenario
{
    private const string QueueTitle = "DropWheel 任务";
    private const int BudgetMs = 150;

    public static int Run(string exe, bool explorer)
    {
        var checks = new Checks();
        var dir = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "m0");
        Directory.CreateDirectory(dir);
        var jpg = Path.Combine(dir, $"m0-{DateTime.Now:HHmmss}.jpg");
        WriteJpeg(jpg, 640, 400);
        var jpgLength = new FileInfo(jpg).Length;
        Log.Info($"app: exe {exe}, source {jpg} ({jpgLength} B)");

        foreach (var stale in Process.GetProcessesByName("DropWheel"))
        {
            using (stale)
            {
                Log.Info($"app: killing a DropWheel already running (pid {stale.Id})");
                stale.Kill();
                stale.WaitForExit(3000);
            }
        }

        var log = new AppLog();
        var started = Log.NowMs;
        using var app = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })
            ?? throw new InvalidOperationException($"could not start {exe}");
        try
        {
            var ready = log.WaitFor(30000, "ready:");
            checks.Check("app starts and reports ready", ready is not null,
                ready is null ? $"no 'ready:' line within 30 s, exited={app.HasExited}" : $"{Log.NowMs - started:F0} ms after start; {log.Find("overlay: prewarm") ?? "no prewarm line"}");
            if (ready is not null)
            {
                Attempt(checks, "convert and undo", () => ConvertAndUndo(checks, log, app, jpg, jpgLength, explorer));
                Attempt(checks, "release off the wheel", () => ReleaseOff(checks, log, jpg, explorer));
            }
        }
        finally
        {
            Quit(checks, log, app);
        }

        return checks.Report($"DropWheel.App M0 ({(explorer ? "Explorer" : "WinForms")} source)");
    }

    private static void ConvertAndUndo(Checks checks, AppLog log, Process app, string jpg, long jpgLength, bool explorer)
    {
        using var source = DragSource.Open(jpg, explorer);
        var mid = source.PressAndDrag();
        var foreground = PInvoke.GetForegroundWindow();
        using var before = Capture(SystemInformation.VirtualScreen);
        log.Mark();
        var wheel = TapShift(checks, log, mid);
        if (wheel is not { } w)
        {
            return;
        }

        var entered = log.WaitFor(2000, "file(s) entered");
        var png = SlotOf(entered, "png");
        checks.Check("drag enters the app with the file and PNG is offered", entered is not null && png >= 0,
            entered ?? "no 'file(s) entered' line within 2 s");
        checks.Check("wheel does not take focus", OwnerPid(PInvoke.GetForegroundWindow()) != app.Id,
            $"foreground \"{Title(PInvoke.GetForegroundWindow())}\" (was \"{Title(foreground)}\")");
        if (png < 0)
        {
            return;
        }

        var target = SectorPoint(w, png);
        Input.MoveSmooth(mid, target, 8, 30);
        Thread.Sleep(250);
        using (var after = Capture(SystemInformation.VirtualScreen))
        {
            Screenshot(after, w);
            var (changed, total) = RimChanged(before, after, w, png);
            checks.Check("wheel disc is drawn (acrylic or its solid fallback)", total > 0 && changed >= total * 0.8,
                $"{changed} of {total} rim samples differ from the screen before the tap");
        }

        var t0 = Log.NowMs;
        Input.LeftUp();
        var enqueued = log.WaitFor(3000, "wheel: enqueued ");
        var closed = log.Find("wheel: closed");
        checks.Check("release on the PNG sector enqueues a conversion", enqueued is not null && closed?.Contains($"dropped on sector {png}", StringComparison.Ordinal) == true,
            $"{enqueued ?? "no 'wheel: enqueued'"}; {closed ?? "wheel never closed"}; {Log.NowMs - t0:F0} ms after button up");
        if (enqueued is null)
        {
            return;
        }

        var label = enqueued[(enqueued.IndexOf("wheel: enqueued ", StringComparison.Ordinal) + "wheel: enqueued ".Length)..];
        var done = log.WaitFor(30000, $"queue: {label} ");
        var output = done is null ? null : OutputsRegex().Match(done) is { Success: true } m ? m.Groups[1].Value : null;
        checks.Check("conversion finishes", output is not null, done ?? "no queue outcome within 30 s");
        if (output is null)
        {
            return;
        }

        checks.Check("PNG lands next to the JPG", SameDir(output, jpg) && output.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(output), output);
        checks.Check("PNG is a real 640x400 PNG", IsPng(output, 640, 400, out var what), what);
        checks.Check("source JPG untouched", File.Exists(jpg) && new FileInfo(jpg).Length == jpgLength, $"{jpg} {(File.Exists(jpg) ? new FileInfo(jpg).Length : -1)} B");
        var fgAfter = PInvoke.GetForegroundWindow();
        checks.Check("focus stays off DropWheel after the drop", OwnerPid(fgAfter) != app.Id, $"foreground \"{Title(fgAfter)}\"");

        // Undo through the task window's button, the way a screen reader would press it.
        log.Mark();
        var pressed = PressUndo(label, out var how);
        var undone = pressed ? log.WaitFor(5000, "queue: undid") : null;
        Checks.Wait(() => !File.Exists(output), 3000);
        checks.Check("Undo button in the task window works", undone is not null && !File.Exists(output), $"{how}; {undone ?? "no 'queue: undid' line"}; png exists={File.Exists(output)}");
        var binned = FileOpsScenario.InRecycleBin(Path.GetDirectoryName(jpg)!);
        checks.Check("undone PNG is in the Recycle Bin, JPG still in place", binned.Contains(Path.GetFileNameWithoutExtension(output)) && File.Exists(jpg),
            $"Recycle Bin items from {Path.GetDirectoryName(jpg)}: [{string.Join(", ", binned)}]; jpg exists={File.Exists(jpg)}");
    }

    private static void ReleaseOff(Checks checks, AppLog log, string jpg, bool explorer)
    {
        var before = Directory.GetFiles(Path.GetDirectoryName(jpg)!);
        using var source = DragSource.Open(jpg, explorer);
        var mid = source.PressAndDrag();
        log.Mark();
        if (TapShift(checks, log, mid) is not { } w)
        {
            return;
        }

        // Over a sector first so the drop target has seen the drag, then back onto the source window.
        log.WaitFor(2000, "file(s) entered");
        var over = SectorPoint(w, 0);
        Input.MoveSmooth(mid, over, 6, 30);
        Input.MoveSmooth(over, source.Grab, 10, 30);
        Thread.Sleep(250);
        Input.LeftUp();
        var closed = log.WaitFor(2000, "wheel: closed");
        Thread.Sleep(1000);
        var after = Directory.GetFiles(Path.GetDirectoryName(jpg)!);
        checks.Check("release off the wheel closes it and converts nothing",
            closed is not null && !closed.Contains("dropped", StringComparison.Ordinal) && log.Find("wheel: enqueued") is null && after.Length == before.Length,
            $"{closed ?? "wheel never closed"}; files {before.Length} -> {after.Length}");
    }

    private readonly record struct Wheel(int X, int Y, double Scale);

    /// <summary>Taps Shift and returns where the app says it put the wheel.</summary>
    private static Wheel? TapShift(Checks checks, AppLog log, Point mid)
    {
        Input.Key(VIRTUAL_KEY.VK_LSHIFT, up: false);
        Thread.Sleep(70);
        Input.Key(VIRTUAL_KEY.VK_LSHIFT, up: true);
        var shown = log.WaitFor(2000, "wheel: shown");
        var frame = log.WaitFor(1000, "overlay: first frame");
        var m = shown is null ? null : ShownRegex().Match(shown);
        if (m is not { Success: true })
        {
            checks.Check("Shift tap mid-drag shows the wheel", false, shown ?? "no 'wheel: shown' within 2 s");
            return null;
        }

        var showMs = double.Parse(m.Groups["ms"].Value, CultureInfo.InvariantCulture);
        var frameMs = frame is not null && FrameRegex().Match(frame) is { Success: true } f ? double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
        checks.Check($"Shift tap mid-drag shows the wheel within {BudgetMs} ms", showMs + frameMs <= BudgetMs,
            $"shown {showMs:F1} ms after the hook saw the tap, first frame {frameMs:F0} ms later");
        var wheel = new Wheel(int.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture));

        // A small move so OLE delivers DragEnter to the drop target that just appeared under the pointer.
        Input.MoveSmooth(mid, new Point(mid.X + 6, mid.Y + 6), 3, 30);
        return wheel;
    }

    /// <summary>Sector of an enabled item in a "slots 0=png,1=webp,2=gif(off)" log line, or -1.</summary>
    private static int SlotOf(string? entered, string item)
    {
        if (entered is null)
        {
            return -1;
        }

        foreach (var slot in SlotsRegex().Match(entered).Groups[1].Value.Split(','))
        {
            var parts = slot.Split('=');
            if (parts.Length == 2 && parts[1].Equals(item, StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(parts[0], CultureInfo.InvariantCulture);
            }
        }

        return -1;
    }

    private static Point SectorPoint(Wheel w, int sector)
    {
        var (dx, dy) = WheelGeometry.SectorCenter(sector);
        return new Point(w.X + (int)Math.Round(dx * w.Scale), w.Y + (int)Math.Round(dy * w.Scale));
    }

    private static bool PressUndo(string label, out string how)
    {
        var window = PInvoke.FindWindow(null, QueueTitle);
        if (window.IsNull)
        {
            how = $"no window titled '{QueueTitle}'";
            return false;
        }

        var uia = (IUIAutomation)new CUIAutomation8();
        var name = $"撤销 {label}";
        var condition = uia.CreatePropertyCondition(UIA_PROPERTY_ID.UIA_NamePropertyId, name);
        IUIAutomationElement? button = null;
        Checks.Wait(() => (button = uia.ElementFromHandle(window).FindFirst(ComTreeScope.TreeScope_Descendants, condition)) is not null, 3000);
        if (button is null)
        {
            how = $"no element named '{name}'";
            return false;
        }

        var invoke = (IUIAutomationInvokePattern)button.GetCurrentPattern(UIA_PATTERN_ID.UIA_InvokePatternId);
        invoke.Invoke();
        how = $"invoked '{name}'";
        return true;
    }

    /// <summary>Closing the task window quits the app; it must exit on its own and say so in its log.</summary>
    private static void Quit(Checks checks, AppLog log, Process app)
    {
        if (app.HasExited)
        {
            checks.Check("app still running at the end", false, $"exited with {app.ExitCode}");
            return;
        }

        var window = PInvoke.FindWindow(null, QueueTitle);
        if (!window.IsNull)
        {
            PInvoke.PostMessage(window, PInvoke.WM_CLOSE, new WPARAM(0), new LPARAM(0));
        }

        var exited = app.WaitForExit(8000);
        if (!exited)
        {
            app.Kill();
        }

        log.Poll();
        checks.Check("closing the task window quits the app", exited && log.Find("shutting down") is not null,
            exited ? $"exit code {app.ExitCode}" : "still running after 8 s, killed");
        checks.Check("app logged no errors", log.Find(" ERROR ") is null, log.Find(" ERROR ") ?? "none");
    }

    /// <summary>Copies a screen rectangle (physical pixels), layered windows included.</summary>
    private static Bitmap Capture(Rectangle area)
    {
        var shot = new Bitmap(area.Width, area.Height);
        using var g = Graphics.FromImage(shot);

        // BitBlt with CAPTUREBLT, as the acrylic spike measured it; Graphics.CopyFromScreen rejects the flag.
        var target = new HDC(g.GetHdc());
        var screen = PInvoke.GetDC(HWND.Null);
        _ = PInvoke.BitBlt(target, 0, 0, area.Width, area.Height, screen, area.X, area.Y, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT);
        _ = PInvoke.ReleaseDC(HWND.Null, screen);
        g.ReleaseHdc();
        return shot;
    }

    /// <summary>Saves the wheel's part of a virtual-screen capture next to the log, for a human to look at (acrylic, labels, hover).</summary>
    private static void Screenshot(Bitmap screen, Wheel w)
    {
        var vs = SystemInformation.VirtualScreen;
        var half = (int)Math.Ceiling((WheelGeometry.OuterRadius + 24) * w.Scale);
        var area = Rectangle.Intersect(new Rectangle(w.X - half - vs.X, w.Y - half - vs.Y, 2 * half, 2 * half), new Rectangle(Point.Empty, screen.Size));
        var path = Path.ChangeExtension(Log.FilePath ?? Path.Combine(Path.GetTempPath(), "app"), null) + "-wheel.png";
        using var shot = screen.Clone(area, screen.PixelFormat);
        shot.Save(path, ImageFormat.Png);
        Log.Info($"app: wheel screenshot {area} -> {path}");
    }

    /// <summary>
    /// Samples the disc's 6 DIP rim outside the sectors every 10° and counts the samples that changed since
    /// before the tap: a disc that did not render leaves the screen as it was. Skips the hovered sector,
    /// where the cursor and the drag image are. Both captures cover the virtual screen.
    /// </summary>
    private static (int Changed, int Total) RimChanged(Bitmap before, Bitmap after, Wheel w, int hovered)
    {
        const double RimRadius = WheelGeometry.OuterRadius + 3;
        var vs = SystemInformation.VirtualScreen;
        var (hx, hy) = WheelGeometry.SectorCenter(hovered);
        var hoveredDeg = Math.Atan2(hy, hx) * 180 / Math.PI;
        int changed = 0, total = 0;
        for (var deg = 0; deg < 360; deg += 10)
        {
            if (Math.Abs(((deg - hoveredDeg + 540) % 360) - 180) < 45)
            {
                continue;
            }

            var a = deg * Math.PI / 180;
            var x = w.X + (int)Math.Round(Math.Cos(a) * RimRadius * w.Scale) - vs.X;
            var y = w.Y + (int)Math.Round(Math.Sin(a) * RimRadius * w.Scale) - vs.Y;
            if (x < 0 || y < 0 || x >= after.Width || y >= after.Height)
            {
                continue;
            }

            var b = before.GetPixel(x, y);
            var c = after.GetPixel(x, y);
            total++;
            if (Math.Max(Math.Abs(b.R - c.R), Math.Max(Math.Abs(b.G - c.G), Math.Abs(b.B - c.B))) >= 16)
            {
                changed++;
            }
        }

        return (changed, total);
    }

    private static void WriteJpeg(string path, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        using (var brush = new LinearGradientBrush(new Rectangle(0, 0, width, height), Color.SteelBlue, Color.Orange, 30f))
        {
            g.FillRectangle(brush, 0, 0, width, height);
            g.FillEllipse(Brushes.White, width / 3, height / 4, width / 3, height / 2);
        }

        bitmap.Save(path, ImageFormat.Jpeg);
    }

    private static bool IsPng(string path, int width, int height, out string what)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var signature = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G';
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream);
            what = $"{bytes.Length} B, signature {(signature ? "PNG" : "not PNG")}, {image.Width}x{image.Height}, decoded as {(image.RawFormat.Equals(ImageFormat.Png) ? "PNG" : image.RawFormat.ToString())}";
            return signature && image.RawFormat.Equals(ImageFormat.Png) && image.Width == width && image.Height == height;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException)
        {
            what = ex.Message;
            return false;
        }
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(a)), Path.GetDirectoryName(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static unsafe uint OwnerPid(HWND window)
    {
        uint pid = 0;
        _ = PInvoke.GetWindowThreadProcessId(window, &pid);
        return pid;
    }

    private static string Title(HWND h)
    {
        Span<char> buf = stackalloc char[256];
        var n = PInvoke.GetWindowText(h, buf);
        return n > 0 ? new string(buf[..n]) : "";
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

    [GeneratedRegex(@"wheel: shown \+(?<ms>[\d.]+) ms after tap, centre (?<x>-?\d+),(?<y>-?\d+) scale (?<s>[\d.]+)")]
    private static partial Regex ShownRegex();

    [GeneratedRegex(@"first frame (\d+) ms")]
    private static partial Regex FrameRegex();

    [GeneratedRegex(@"slots (\S+)")]
    private static partial Regex SlotsRegex();

    [GeneratedRegex(@" done in \d+ ms, outputs (.+)$")]
    private static partial Regex OutputsRegex();
}
