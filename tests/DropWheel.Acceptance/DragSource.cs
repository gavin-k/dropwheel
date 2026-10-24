using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace DropWheel.Acceptance;

/// <summary>
/// Where a drag starts: either this exe's own WinForms source window (<c>--dragsource</c>, which also
/// reports the effect DoDragDrop returned) or a real File Explorer window found through UI Automation.
/// </summary>
internal sealed class DragSource : IDisposable
{
    private readonly Process? _child;
    private readonly HWND _explorerWindow;

    private DragSource(Point grab, Process? child, HWND explorerWindow)
    {
        Grab = grab;
        _child = child;
        _explorerWindow = explorerWindow;
    }

    /// <summary>Screen point (physical pixels) to press on to drag the file.</summary>
    public Point Grab { get; }

    public static DragSource Open(string file, bool explorer)
    {
        if (explorer)
        {
            var (window, item) = ExplorerSource.Open(file, TimeSpan.FromSeconds(25));
            return new(new Point(item.X + item.Width / 2, item.Y + item.Height / 2), null, window);
        }

        var vs = SystemInformation.VirtualScreen;
        var at = new Point(vs.X + 120, vs.Y + 160);
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--dragsource \"{file}\" --at {at.X},{at.Y}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("could not start the drag source");
        var ready = ReadLine(child, "READY", TimeSpan.FromSeconds(10)) ?? throw new TimeoutException("drag source never printed READY");
        var p = ready.Split(' ');
        var bounds = new Rectangle(int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4]));
        Log.Info($"source: window {bounds}");
        return new(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2 + 20), child, default);
    }

    /// <summary>The effect the WinForms source's DoDragDrop returned, or null for Explorer / no answer.</summary>
    public string? Result(TimeSpan timeout) => _child is null ? null : ReadLine(_child, "RESULT", timeout);

    /// <summary>
    /// Press on the source and drag far enough that the system drag is under way, towards the middle of the
    /// screen: near an edge the app shifts the wheel to keep it on screen, and it must not end up over the source.
    /// </summary>
    public Point PressAndDrag(int distancePx = 520)
    {
        Thread.Sleep(400);
        Input.MoveTo(Grab.X, Grab.Y);
        Thread.Sleep(150);
        Input.LeftDown();
        Thread.Sleep(120);
        var area = Screen.FromPoint(Grab).WorkingArea;
        var direction = Grab.X > area.X + area.Width / 2 ? -1 : 1;
        var mid = new Point(Grab.X + direction * distancePx, Grab.Y);
        Input.MoveSmooth(Grab, mid, 14, 25);
        Thread.Sleep(250);
        return mid;
    }

    public void Dispose()
    {
        Input.LeftUp();
        try
        {
            if (_child is { HasExited: false })
            {
                _child.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }

        _child?.Dispose();
        ExplorerSource.CloseAndWait(_explorerWindow.IsNull ? ExplorerSource.LastWindow : _explorerWindow);
    }

    /// <summary>Effective DPI / 96 of the monitor containing <paramref name="pt"/>.</summary>
    public static double ScaleAt(Point pt)
    {
        var monitor = PInvoke.MonitorFromPoint(pt, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded ? dpi / 96.0 : 1.0;
    }

    private static string? ReadLine(Process p, string prefix, TimeSpan timeout)
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

            if (task.Result is not { } line)
            {
                return null;
            }

            Log.Info($"source> {line}");
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line;
            }
        }

        return null;
    }
}
