using System.Diagnostics;
using DropWheel.App.Diagnostics;
using DropWheel.App.Overlay;
using DropWheel.Core.Conversion;
using DropWheel.Core.Input;
using DropWheel.Core.Jobs;
using DropWheel.Core.Settings;
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
/// One drag's worth of wheel: a modifier tap, or a finger or pen held still, opens it at the pointer, DragEnter on the drop target host
/// says which files are being dragged, a drop on a sector enqueues the conversion or the file action.
/// The wheels are Convert, Tools and Actions; see <see cref="Modes"/>. Letters and digits typed on the
/// Convert wheel search every target the files can become, and the sectors show the best matches.
///
/// The wheel closes on Dropped / Canceled from the host, on the hook seeing the button go up (after a
/// grace period, because the hook sees the button-up before OLE delivers the drop), and on a watchdog
/// that polls the physical button, so a lost hook cannot leave the wheel on screen.
///
/// Threads: the Invoked events, Typed and PhaseChanged come from the hook thread, Entered / Hovered / Dropped /
/// Canceled from the host thread. Everything that touches a window is queued to the UI thread; the session
/// is only replaced on the UI thread. The model is rebuilt on the host thread when the files arrive and on
/// the UI thread when the search changes, under the session's lock.
/// </summary>
internal sealed class WheelController : IDisposable
{
    private static readonly TimeSpan IdleGrace = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the pointer rests on a group sector before its ring opens, and how long it is off the group and its ring before the ring closes.</summary>
    private static readonly TimeSpan DwellDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RingCloseDelay = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherQueue _ui;
    private readonly DragMonitor _monitor;
    private readonly DropTargetHost _host;
    private readonly WheelWindow _wheel;
    private readonly WheelBuilder _builder;
    private readonly JobQueue _queue;
    private readonly IRecentItems _recent;
    private readonly SettingsStore _settings;
    private readonly ModifierTapper _tapper;
    private readonly LongPress _longPress;
    private readonly TypeAhead _typeAhead;
    private readonly DispatcherQueueTimer _idleGrace;
    private readonly DispatcherQueueTimer _watchdog;
    private readonly DispatcherQueueTimer _dwell;
    private readonly DispatcherQueueTimer _ringClose;
    private readonly LayoutStore? _layout;
    private volatile Session? _session;
    private int _buttonUpReadings;
    private int _pendingGroup = -1;

    public WheelController(
        DispatcherQueue ui,
        DragMonitor monitor,
        DropTargetHost host,
        WheelWindow wheel,
        WheelBuilder builder,
        JobQueue queue,
        IRecentItems recent,
        SettingsStore settings,
        ModifierTapper tapper,
        LongPress longPress,
        TypeAhead typeAhead,
        LayoutStore? layout = null)
    {
        _ui = ui;
        _monitor = monitor;
        _host = host;
        _wheel = wheel;
        _builder = builder;
        _queue = queue;
        _recent = recent;
        _settings = settings;
        _tapper = tapper;
        _longPress = longPress;
        _typeAhead = typeAhead;
        _layout = layout;

        _idleGrace = ui.CreateTimer();
        _idleGrace.Interval = IdleGrace;
        _idleGrace.IsRepeating = false;
        _idleGrace.Tick += (_, _) => Close("button up without a drop");
        _watchdog = ui.CreateTimer();
        _watchdog.Interval = WatchdogPeriod;
        _watchdog.Tick += (_, _) => CheckButton();
        _dwell = ui.CreateTimer();
        _dwell.Interval = DwellDelay;
        _dwell.IsRepeating = false;
        _dwell.Tick += (_, _) => OpenRing();
        _ringClose = ui.CreateTimer();
        _ringClose.Interval = RingCloseDelay;
        _ringClose.IsRepeating = false;
        _ringClose.Tick += (_, _) => CloseRing("pointer left it");

        _tapper.Invoked += OnTapped;
        _longPress.Invoked += OnHeld;
        _typeAhead.Typed += OnTyped;
        _monitor.PhaseChanged += OnPhaseChanged;
        _host.Entered += OnEntered;
        _host.Noted += OnNoted;
        _host.Hovered += OnHovered;
        _host.HubReached += OnHubReached;
        _host.Dropped += OnDropped;
        _host.Canceled += OnCanceled;
    }

    /// <summary>True from the moment the wheel opens until it closes; any thread.</summary>
    public bool IsOpen => _session is not null;

    /// <summary>The wheels, in the order Settings lists their keys.</summary>
    public static IReadOnlyList<WheelMode> Modes { get; } = [WheelMode.Convert, WheelMode.Tools, WheelMode.Actions];

    public void Dispose()
    {
        _tapper.Invoked -= OnTapped;
        _longPress.Invoked -= OnHeld;
        _typeAhead.Typed -= OnTyped;
        _monitor.PhaseChanged -= OnPhaseChanged;
        _host.Entered -= OnEntered;
        _host.Noted -= OnNoted;
        _host.Hovered -= OnHovered;
        _host.HubReached -= OnHubReached;
        _host.Dropped -= OnDropped;
        _host.Canceled -= OnCanceled;
        _idleGrace.Stop();
        _watchdog.Stop();
        _dwell.Stop();
        _ringClose.Stop();
    }

    // ---- hook thread ------------------------------------------------------------------------------

    private void OnTapped(WheelMode mode) => OpenFromHook(mode, "tap");

    private void OnHeld(WheelMode mode) => OpenFromHook(mode, "long press");

    private void OpenFromHook(WheelMode mode, string by)
    {
        var tappedAt = Stopwatch.GetTimestamp();
        var point = _monitor.LastPoint;
        _ui.TryEnqueue(() => Open(mode, point, tappedAt, by));
    }

    private void OnTyped(char c) => _ui.TryEnqueue(() => Type(c));

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
        // hit-tests against the real enabled sectors. Anything typed since the tap is searched already.
        session.Paths = paths;
        var model = Rebuild(session)!;
        FileLog.Info($"wheel: {paths.Count} file(s) entered +{Stopwatch.GetElapsedTime(session.TappedAt).TotalMilliseconds:F0} ms after {session.By}, slots {Slots(model)}");
        _ui.TryEnqueue(() =>
        {
            // A search typed meanwhile has already shown a newer model.
            if (_session == session && session.Model == model)
            {
                _wheel.SetModel(model);
            }
        });
    }

    private void OnNoted(string note) => FileLog.Info($"wheel: {note}");

    private void OnHovered(WheelHit hit) => _ui.TryEnqueue(() => Hover(hit));

    private void OnHubReached()
    {
        var session = _session;
        FileLog.Info("wheel: pointer reached the hub, sectors are live");
        _ui.TryEnqueue(() =>
        {
            if (_session == session)
            {
                _wheel.SetHubHint(false);
            }
        });
    }

    private void OnDropped(IReadOnlyList<string> paths, WheelHit hit)
    {
        var session = _session;
        var slot = SlotUnder(session, hit);
        _ui.TryEnqueue(() =>
        {
            Close($"dropped on {hit}");
            if (slot is null)
            {
                FileLog.Error($"wheel: drop on {hit} without a slot");
                return;
            }

            if (slot.Operation is null)
            {
                // A group sector is never enabled, so the host refuses the drop before it gets here; defensive.
                FileLog.Info($"wheel: drop on group {hit} ({slot.Label}) ignored");
                return;
            }

            var label = paths.Count == 1 ? $"{Path.GetFileName(paths[0])} → {slot.Label}" : L.T("{0} 项 → {1}", paths.Count, slot.Label);

            // File actions never count against the daily quota, and only conversions have recent items.
            _queue.Enqueue(session!.Settings.Request(paths, slot.ItemId), label, slot.Operation, countsTowardQuota: session.Mode != WheelMode.Actions);
            if (session.Mode == WheelMode.Convert)
            {
                _recent.Touch(FormatRegistry.FromPath(paths[0])?.Kind ?? MediaKind.Unknown, session.Mode, slot.ItemId);
            }

            FileLog.Info($"wheel: enqueued {label}");
        });
    }

    private void OnCanceled() => _ui.TryEnqueue(() => Close("canceled"));

    /// <summary>The slot a hit lands on: a sector's, or an item of the ring open at the time.</summary>
    private static WheelSlot? SlotUnder(Session? session, WheelHit hit)
    {
        if (session?.Model is not { } model)
        {
            return null;
        }

        return hit.Ring switch
        {
            WheelRing.Sector => model.SlotAt(hit.Index),
            WheelRing.Child => model.SlotAt(session.OpenGroup)?.Children is { } children && hit.Index >= 0 && hit.Index < children.Count ? children[hit.Index] : null,
            _ => null,
        };
    }

    // ---- UI thread --------------------------------------------------------------------------------

    /// <param name="by">What opened it, for the log: "tap" or "long press".</param>
    private void Open(WheelMode mode, (int X, int Y) point, long tappedAt, string by)
    {
        if (_session is not null || _monitor.Phase == DragPhase.Idle)
        {
            return;
        }

        // Near an edge the wheel is shifted away from the pointer and may open with a sector under it.
        // Then the sectors wait until the pointer has been on the hub, so an immediate release cancels.
        // A wheel with groups is placed by its ring's footprint, so an open ring stays on screen too.
        var ring = _layout?.HasGroups(mode) == true;
        var (x, y, scale) = Place(point, WheelWindow.SizeFor(ring));
        var onHub = WheelGeometry.OnHub((point.X - x) / scale, (point.Y - y) / scale);
        var placement = new WheelPlacement(x, y, scale, new bool[WheelModel.SlotCount], onHub);
        _session = new Session(mode, placement, tappedAt, by, _settings.Current);
        _typeAhead.Listening = mode == WheelMode.Convert;
        _pendingGroup = -1;

        // Order matters: the host has to end up above the wheel. If the wheel were on top, OLE would see
        // the drag leave the host and the host would report a cancel.
        _wheel.SetHubHint(!onHub);
        _wheel.ShowAt(x, y, scale, ring);
        _host.Show(placement);
        _buttonUpReadings = 0;
        _watchdog.Start();
        FileLog.Info($"wheel: shown +{Stopwatch.GetElapsedTime(tappedAt).TotalMilliseconds:F1} ms after {by}, centre {x},{y} scale {scale:0.##} (pointer {point.X},{point.Y}{(onHub ? "" : ", off the hub: sectors wait for it")})");
    }

    private void Close(string why)
    {
        _idleGrace.Stop();
        _watchdog.Stop();
        _dwell.Stop();
        _ringClose.Stop();
        if (_session is null)
        {
            return;
        }

        _session = null;
        _typeAhead.Listening = false;
        _host.Hide();
        _wheel.Hide();
        FileLog.Info($"wheel: closed ({why})");
    }

    private void Type(char c)
    {
        var session = _session;
        if (session is null || session.Mode != WheelMode.Convert)
        {
            return;
        }

        WheelModel? model;
        lock (session.Gate)
        {
            var query = session.Query;
            var next = c == TypeAhead.Backspace ? query[..Math.Max(0, query.Length - 1)] : query.Length < WheelModel.MaxQuery ? query + c : query;
            if (next == query)
            {
                return;
            }

            session.Query = next;
            model = Rebuild(session);
        }

        // Before the files are known there is nothing to search yet; DragEnter builds with the query.
        if (model is not null)
        {
            _wheel.SetModel(model);
            FileLog.Info($"wheel: search \"{session.Query}\", slots {Slots(model)}");
        }
    }

    /// <summary>
    /// The pointer is somewhere new. Resting on a group sector opens its ring after <see cref="DwellDelay"/>;
    /// leaving the group and its ring for anywhere but the hub closes the ring after <see cref="RingCloseDelay"/>,
    /// so a pointer cutting across the gap between them does not shut it; the hub closes it at once.
    /// </summary>
    private void Hover(WheelHit hit)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        _wheel.SetHovered(hit);
        var group = hit.Ring == WheelRing.Sector && session.Model?.SlotAt(hit.Index) is { IsGroup: true } ? hit.Index : -1;
        var stays = hit.Ring == WheelRing.Child || (hit.Ring == WheelRing.Sector && hit.Index == session.OpenGroup);
        _dwell.Stop();
        _ringClose.Stop();
        if (hit.Ring == WheelRing.Hub)
        {
            CloseRing("pointer on the hub");
            return;
        }

        if (group >= 0 && group != session.OpenGroup)
        {
            _pendingGroup = group;
            _dwell.Start();
        }

        if (session.OpenGroup >= 0 && !stays)
        {
            _ringClose.Start();
        }
    }

    private void OpenRing()
    {
        var session = _session;
        var index = _pendingGroup;
        if (session is null || session.Model?.SlotAt(index) is not { Children: { } children } slot)
        {
            return;
        }

        lock (session.Gate)
        {
            session.OpenGroup = index;
            session.Ring = new RingPlacement(index, [.. children.Select(c => c.Availability.IsAvailable)]);
            ShowHost(session);
        }

        _wheel.SetRing(index, children);
        FileLog.Info($"wheel: ring open on sector {index} ({slot.Label}): {string.Join(",", children.Select(c => $"{c.Index}={c.ItemId}{(c.Availability.IsAvailable ? "" : "(off)")}"))}");
    }

    private void CloseRing(string why)
    {
        var session = _session;
        if (session is null || session.OpenGroup < 0)
        {
            return;
        }

        lock (session.Gate)
        {
            session.OpenGroup = -1;
            session.Ring = null;
            ShowHost(session);
        }

        _wheel.SetRing(-1, null);
        FileLog.Info($"wheel: ring closed ({why})");
    }

    /// <summary>Tells the host what is live now: the enabled sectors and the open ring. Under the session's lock.</summary>
    private void ShowHost(Session session) => _host.Show(session.Placement with { EnabledSectors = session.Enabled, Ring = session.Ring });

    /// <summary>
    /// Builds the session's model for its files and search and tells the host which sectors are live. Null
    /// before the files are known. Any thread; the lock keeps the model and the host's sectors from two
    /// builds from interleaving.
    /// </summary>
    private WheelModel? Rebuild(Session session)
    {
        lock (session.Gate)
        {
            if (session.Paths is not { } paths)
            {
                return null;
            }

            var model = _builder.Build(paths, session.Mode, session.Settings, session.Query);
            session.Model = model;

            // A group sector is never enabled: a release on it cancels, and its ring is where the drop goes.
            session.Enabled = Enumerable.Range(0, WheelModel.SlotCount).Select(i => model.SlotAt(i) is { Availability.IsAvailable: true, Operation: not null }).ToArray();
            if (session.OpenGroup >= 0)
            {
                session.Ring = model.SlotAt(session.OpenGroup)?.Children is { } children ? new RingPlacement(session.OpenGroup, [.. children.Select(c => c.Availability.IsAvailable)]) : null;
            }

            ShowHost(session);
            return model;
        }
    }

    /// <summary>"0=png,1=webp,2=gif(off)", the form the acceptance run reads.</summary>
    private static string Slots(WheelModel model)
        => model.Slots.Count == 0 ? "none" : string.Join(",", model.Slots.Select(s => $"{s.Index}={s.ItemId}{(s.Availability.IsAvailable ? "" : "(off)")}"));

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

    /// <summary>Centre on the pointer, pulled inside the work area of the pointer's monitor by the window's size in DIPs.</summary>
    internal static (int X, int Y, double Scale) Place((int X, int Y) point, double sizeDip)
    {
        var monitor = PInvoke.MonitorFromPoint(new System.Drawing.Point(point.X, point.Y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var scale = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded ? dpi / 96.0 : 1.0;
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return (point.X, point.Y, scale);
        }

        var half = (int)Math.Ceiling(sizeDip * scale / 2);
        var work = info.rcWork;
        return (
            Math.Clamp(point.X, work.left + half, Math.Max(work.left + half, work.right - half)),
            Math.Clamp(point.Y, work.top + half, Math.Max(work.top + half, work.bottom - half)),
            scale);
    }

    /// <param name="settings">The snapshot the wheel was built with; the drop uses the same one.</param>
    private sealed class Session(WheelMode mode, WheelPlacement placement, long tappedAt, string by, AppSettings settings)
    {
        public AppSettings Settings { get; } = settings;
        public WheelMode Mode { get; } = mode;
        public WheelPlacement Placement { get; } = placement;
        public long TappedAt { get; } = tappedAt;
        public string By { get; } = by;
        public volatile WheelModel? Model;

        /// <summary>The files, once DragEnter said which.</summary>
        public volatile IReadOnlyList<string>? Paths;

        /// <summary>Which sectors take a drop, from the last build; see <see cref="Rebuild"/>.</summary>
        public IReadOnlyList<bool> Enabled { get; set; } = placement.EnabledSectors;

        /// <summary>The sector whose ring is open, or -1, and the ring the host hit-tests. UI thread writes, under <see cref="Gate"/>.</summary>
        public volatile int OpenGroup = -1;

        public volatile RingPlacement? Ring;

        /// <summary>Guards <see cref="Query"/> and each rebuild of <see cref="Model"/>.</summary>
        public Lock Gate { get; } = new();

        public string Query { get; set; } = "";
    }
}
