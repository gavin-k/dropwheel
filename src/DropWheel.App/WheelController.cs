using System.Diagnostics;
using DropWheel.App.Diagnostics;
using DropWheel.App.Overlay;
using DropWheel.Core.Conversion;
using DropWheel.Core.Input;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;
using DropWheel.Shell;
using Microsoft.UI.Dispatching;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DropWheel.App;

/// <summary>
/// One drag's worth of wheel: a modifier tap opens it at the pointer, DragEnter on the drop target host
/// says which files are being dragged, a drop on a sector enqueues the conversion. M0 is the Convert
/// wheel only.
///
/// The wheel closes on Dropped / Canceled from the host, on the hook seeing the button go up (after a
/// grace period, because the hook sees the button-up before OLE delivers the drop), and on a watchdog
/// that polls the physical button, so a lost hook cannot leave the wheel on screen.
///
/// Threads: Invoked and PhaseChanged come from the hook thread, Entered / Hovered / Dropped / Canceled
/// from the host thread. Everything that touches a window is queued to the UI thread; the session is
/// only replaced on the UI thread.
/// </summary>
internal sealed class WheelController : IDisposable
{
    private static readonly TimeSpan IdleGrace = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherQueue _ui;
    private readonly DragMonitor _monitor;
    private readonly DropTargetHost _host;
    private readonly WheelWindow _wheel;
    private readonly WheelBuilder _builder;
    private readonly JobQueue _queue;
    private readonly IRecentItems _recent;
    private readonly ModifierTapper _tapper;
    private readonly DispatcherQueueTimer _idleGrace;
    private readonly DispatcherQueueTimer _watchdog;
    private volatile Session? _session;
    private int _buttonUpReadings;

    public WheelController(
        DispatcherQueue ui,
        DragMonitor monitor,
        DropTargetHost host,
        WheelWindow wheel,
        WheelBuilder builder,
        JobQueue queue,
        IRecentItems recent,
        ModifierTapper tapper)
    {
        _ui = ui;
        _monitor = monitor;
        _host = host;
        _wheel = wheel;
        _builder = builder;
        _queue = queue;
        _recent = recent;
        _tapper = tapper;

        _idleGrace = ui.CreateTimer();
        _idleGrace.Interval = IdleGrace;
        _idleGrace.IsRepeating = false;
        _idleGrace.Tick += (_, _) => Close("button up without a drop");
        _watchdog = ui.CreateTimer();
        _watchdog.Interval = WatchdogPeriod;
        _watchdog.Tick += (_, _) => CheckButton();

        _tapper.Invoked += OnInvoked;
        _monitor.PhaseChanged += OnPhaseChanged;
        _host.Entered += OnEntered;
        _host.Hovered += OnHovered;
        _host.Dropped += OnDropped;
        _host.Canceled += OnCanceled;
    }

    public void Dispose()
    {
        _tapper.Invoked -= OnInvoked;
        _monitor.PhaseChanged -= OnPhaseChanged;
        _host.Entered -= OnEntered;
        _host.Hovered -= OnHovered;
        _host.Dropped -= OnDropped;
        _host.Canceled -= OnCanceled;
        _idleGrace.Stop();
        _watchdog.Stop();
    }

    // ---- hook thread ------------------------------------------------------------------------------

    private void OnInvoked(WheelMode mode)
    {
        var tappedAt = Stopwatch.GetTimestamp();
        var point = _monitor.LastPoint;
        _ui.TryEnqueue(() => Open(mode, point, tappedAt));
    }

    private void OnPhaseChanged(DragPhase phase)
    {
        if (phase == DragPhase.Idle)
        {
            _ui.TryEnqueue(() =>
            {
                if (_session is not null)
                {
                    _idleGrace.Start();
                }
            });
        }
    }

    // ---- host thread ------------------------------------------------------------------------------

    private void OnEntered(IReadOnlyList<string> paths)
    {
        var session = _session;
        if (session is null || paths.Count == 0)
        {
            return;
        }

        // Built here, before DragEnter computes its first effect, so the very first DragOver already
        // hit-tests against the real enabled sectors.
        var model = _builder.Build(paths, session.Mode, outputDirectory: "");
        session.Model = model;
        var enabled = Enumerable.Range(0, WheelModel.SlotCount).Select(i => i < model.Slots.Count && model.Slots[i].Availability.IsAvailable).ToArray();
        _host.Show(session.Placement with { EnabledSectors = enabled });
        FileLog.Info($"wheel: {paths.Count} file(s) entered +{Stopwatch.GetElapsedTime(session.TappedAt).TotalMilliseconds:F0} ms after tap, slots {string.Join(",", model.Slots.Select(s => $"{s.Index}={s.ItemId}{(s.Availability.IsAvailable ? "" : "(off)")}"))}");
        _ui.TryEnqueue(() =>
        {
            if (_session == session)
            {
                _wheel.SetModel(model);
            }
        });
    }

    private void OnHovered(int sector) => _ui.TryEnqueue(() => _wheel.SetHovered(sector));

    private void OnDropped(IReadOnlyList<string> paths, int sector)
    {
        var session = _session;
        var slot = session?.Model is { } model && sector >= 0 && sector < model.Slots.Count ? model.Slots[sector] : null;
        _ui.TryEnqueue(() =>
        {
            Close($"dropped on sector {sector}");
            if (slot is null)
            {
                FileLog.Error($"wheel: drop on sector {sector} without a slot");
                return;
            }

            var label = paths.Count == 1 ? $"{Path.GetFileName(paths[0])} → {slot.Label}" : $"{paths.Count} 个文件 → {slot.Label}";
            _queue.Enqueue(new ConversionRequest(paths, slot.ItemId, OutputDirectory: ""), label);
            _recent.Touch(FormatRegistry.FromPath(paths[0])?.Kind ?? MediaKind.Unknown, WheelMode.Convert, slot.ItemId);
            FileLog.Info($"wheel: enqueued {label}");
        });
    }

    private void OnCanceled() => _ui.TryEnqueue(() => Close("canceled"));

    // ---- UI thread --------------------------------------------------------------------------------

    private void Open(WheelMode mode, (int X, int Y) point, long tappedAt)
    {
        if (mode != WheelMode.Convert)
        {
            FileLog.Info($"wheel: {mode} tap ignored, M0 has the Convert wheel only");
            return;
        }

        if (_session is not null || _monitor.Phase == DragPhase.Idle)
        {
            return;
        }

        var (x, y, scale) = Place(point);
        var placement = new WheelPlacement(x, y, scale, new bool[WheelModel.SlotCount]);
        _session = new Session(mode, placement, tappedAt);

        // Order matters: the host has to end up above the wheel. If the wheel were on top, OLE would see
        // the drag leave the host and the host would report a cancel.
        _wheel.ShowAt(x, y, scale);
        _host.Show(placement);
        _buttonUpReadings = 0;
        _watchdog.Start();
        FileLog.Info($"wheel: shown +{Stopwatch.GetElapsedTime(tappedAt).TotalMilliseconds:F1} ms after tap, centre {x},{y} scale {scale:0.##} (pointer {point.X},{point.Y})");
    }

    private void Close(string why)
    {
        _idleGrace.Stop();
        _watchdog.Stop();
        if (_session is null)
        {
            return;
        }

        _session = null;
        _host.Hide();
        _wheel.Hide();
        FileLog.Info($"wheel: closed ({why})");
    }

    private void CheckButton()
    {
        var button = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0 ? VIRTUAL_KEY.VK_RBUTTON : VIRTUAL_KEY.VK_LBUTTON;
        var down = (PInvoke.GetAsyncKeyState((int)button) & 0x8000) != 0;
        _buttonUpReadings = down ? 0 : _buttonUpReadings + 1;
        if (_buttonUpReadings >= 2)
        {
            Close("watchdog: button is up");
        }
    }

    /// <summary>Centre on the pointer, pulled inside the work area of the pointer's monitor.</summary>
    internal static (int X, int Y, double Scale) Place((int X, int Y) point)
    {
        var monitor = PInvoke.MonitorFromPoint(new System.Drawing.Point(point.X, point.Y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var scale = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded ? dpi / 96.0 : 1.0;
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return (point.X, point.Y, scale);
        }

        var half = (int)Math.Ceiling(WheelWindow.Size * scale / 2);
        var work = info.rcWork;
        return (
            Math.Clamp(point.X, work.left + half, Math.Max(work.left + half, work.right - half)),
            Math.Clamp(point.Y, work.top + half, Math.Max(work.top + half, work.bottom - half)),
            scale);
    }

    private sealed class Session(WheelMode mode, WheelPlacement placement, long tappedAt)
    {
        public WheelMode Mode { get; } = mode;
        public WheelPlacement Placement { get; } = placement;
        public long TappedAt { get; } = tappedAt;
        public volatile WheelModel? Model;
    }
}
