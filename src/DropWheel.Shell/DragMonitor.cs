using DropWheel.Core.Input;
using DropWheel.Core.Settings;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DropWheel.Shell;

public enum DragPhase { Idle, MaybeDragging, Dragging }

/// <summary>
/// The one hook thread (ADR-0002 and its revision): owns WH_MOUSE_LL and WH_KEYBOARD_LL and polls
/// for the Shell's drag image while the left button is down.
///   1. Left button down, then a move past the threshold → <see cref="DragPhase.MaybeDragging"/>
///   2. A top-level "SysDragImage" window while the button is down → <see cref="DragPhase.Dragging"/>
///   3. Left button up → <see cref="DragPhase.Idle"/>
/// Modifier keys go to <see cref="ModifierTapper"/> with "dragging" meaning any phase but Idle: a drag
/// without a drag image (WinForms DoDragDrop, or a run where SysDragImage never showed) still has to
/// open the wheel. Win key events go through <see cref="WinKeyGate"/>, other keys through
/// <see cref="TypeAhead"/>, which takes them while the Convert wheel is open. The pointer moves of a
/// drag go to <see cref="LongPress"/>, which says when to start its timer, and those of a drag with a
/// drag image to <see cref="EdgeDock"/>, which says when the pointer is against an edge of the screen.
///
/// Windows silently removes a low-level hook whose callback runs past LowLevelHooksTimeout, so the
/// callbacks only update state, and <see cref="PhaseChanged"/>, <see cref="EdgeReached"/> and the Invoked
/// events of the tapper and the long-press are raised inline on this thread: handlers must post to their
/// own thread and return.
/// </summary>
public sealed unsafe class DragMonitor(ModifierTapper tapper, WinKeyGate winKeys, TypeAhead typeAhead, LongPress? longPress = null, EdgeDock? edgeDock = null)
    : IDisposable
{
    private const int ThresholdPx = 8;
    private const uint PollMs = 25;

    private Thread? _thread;
    private uint _threadId;
    private HOOKPROC? _mouseProc;
    private HOOKPROC? _keyboardProc;
    private nuint _pollTimer;
    private nuint _holdTimer;
    private int _slop;
    private bool _buttonDown;
    private System.Drawing.Point _downAt;
    private RECT _monitor;
    private int _edgeZone;
    private long _pointer;
    private volatile DragPhase _phase;

    /// <summary>Raised on the hook thread, inside the hook callback.</summary>
    public event Action<DragPhase>? PhaseChanged;

    /// <summary>
    /// The pointer of a drag with a drag image arrived at the left or right edge of the screen, at x, y in
    /// physical pixels. Raised on the hook thread, inside the hook callback.
    /// </summary>
    public event Action<DockSide, int, int>? EdgeReached;

    /// <summary>A handler threw on the hook thread. The exception is not rethrown: it would cross into user32.</summary>
    public event Action<Exception>? Faulted;

    public DragPhase Phase => _phase;

    /// <summary>The last pointer position the mouse hook saw, in physical pixels.</summary>
    public (int X, int Y) LastPoint
    {
        get
        {
            var packed = Volatile.Read(ref _pointer);
            return ((int)(packed >> 32), (int)packed);
        }
    }

    /// <summary>Installs both hooks on a new thread. Blocks until done; throws if either hook fails.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready, ref failure)) { IsBackground = true, Name = "DragMonitor" };
        _thread.Start();
        ready.Wait();
        if (failure is not null)
        {
            _thread = null;
            throw failure;
        }
    }

    public void Dispose()
    {
        if (_thread is null)
        {
            return;
        }

        PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
        _thread.Join(2000);
        _thread = null;
    }

    private void Run(ManualResetEventSlim ready, ref Exception? failure)
    {
        _threadId = PInvoke.GetCurrentThreadId();
        _mouseProc = MouseProc;
        _keyboardProc = KeyboardProc;
        using var user32 = PInvoke.LoadLibrary("user32.dll");
        using var mouse = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, _mouseProc, user32, 0);
        using var keyboard = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _keyboardProc, user32, 0);
        if (mouse.IsInvalid || keyboard.IsInvalid)
        {
            failure = new System.ComponentModel.Win32Exception();
            ready.Set();
            return;
        }

        ready.Set();
        while (PInvoke.GetMessage(out var msg, default, 0, 0))
        {
            if (msg.message == PInvoke.WM_TIMER && msg.wParam.Value == _pollTimer)
            {
                PollDragImage();
                continue;
            }

            if (msg.message == PInvoke.WM_TIMER && _holdTimer != 0 && msg.wParam.Value == _holdTimer)
            {
                HoldElapsed();
                continue;
            }

            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }

        StopPolling();
        StopHold();
    }

    private LRESULT MouseProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            try
            {
                var info = (MSLLHOOKSTRUCT*)lParam.Value;
                var pt = info->pt;
                Volatile.Write(ref _pointer, ((long)pt.X << 32) | (uint)pt.Y);
                var message = (uint)wParam.Value;
                switch (message)
                {
                    case PInvoke.WM_LBUTTONDOWN:
                        _buttonDown = true;
                        _downAt = pt;
                        StopPolling();
                        _pollTimer = PInvoke.SetTimer(default, 0, PollMs, null);
                        break;
                    case PInvoke.WM_MOUSEMOVE when _buttonDown && _phase == DragPhase.Idle
                        && (Math.Abs(pt.X - _downAt.X) > ThresholdPx || Math.Abs(pt.Y - _downAt.Y) > ThresholdPx):
                        _slop = SlopAt(pt);
                        Transition(DragPhase.MaybeDragging);
                        break;
                    case PInvoke.WM_LBUTTONUP:
                        EndDrag();
                        break;
                }

                if (message == PInvoke.WM_MOUSEMOVE && _phase != DragPhase.Idle && longPress is not null
                    && longPress.OnMove(pt.X, pt.Y, LongPress.FromTouchOrPen(info->dwExtraInfo), _slop) && longPress.Hold is { } hold)
                {
                    _holdTimer = PInvoke.SetTimer(default, _holdTimer, (uint)hold.TotalMilliseconds, null); // the same id restarts it
                }

                if (message == PInvoke.WM_MOUSEMOVE && _phase == DragPhase.Dragging && edgeDock is { Enabled: true })
                {
                    CheckEdge(pt);
                }
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(ex);
            }
        }

        return PInvoke.CallNextHookEx(default, code, wParam, lParam);
    }

    private LRESULT KeyboardProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            try
            {
                var vk = (VIRTUAL_KEY)((KBDLLHOOKSTRUCT*)lParam.Value)->vkCode;
                var down = (uint)wParam.Value is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;

                // A button-up the hook never saw (it was skipped, or landed on the secure desktop) would leave
                // the phase stuck, a Shift tap opening the wheel with nothing dragged and every Win press
                // swallowed. The physical button settles it.
                if (down && _phase != DragPhase.Idle && !ButtonIsDown())
                {
                    EndDrag();
                }

                var dragging = _phase != DragPhase.Idle;
                var modifier = ModifierOf(vk);
                if (modifier == Modifiers.None)
                {
                    if (down)
                    {
                        tapper.OnOtherKey();
                    }

                    if (typeAhead.Swallow((int)vk, down, HeldModifiers()))
                    {
                        return new LRESULT(1);
                    }
                }
                else
                {
                    tapper.OnModifier(modifier, down, dragging, (int)vk);
                    if (modifier == Modifiers.Win && winKeys.Swallow(down, dragging))
                    {
                        return new LRESULT(1);
                    }
                }
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(ex);
            }
        }

        return PInvoke.CallNextHookEx(default, code, wParam, lParam);
    }

    private void PollDragImage()
    {
        if (_buttonDown && !PInvoke.FindWindowEx(default, default, "SysDragImage", null).IsNull)
        {
            StopPolling();
            try
            {
                Transition(DragPhase.Dragging);
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(ex);
            }
        }
    }

    private void HoldElapsed()
    {
        StopHold();
        if (_phase == DragPhase.Idle)
        {
            return;
        }

        try
        {
            longPress?.Elapsed();
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
        }
    }

    private void EndDrag()
    {
        _buttonDown = false;
        StopPolling();
        StopHold();
        longPress?.Reset();
        edgeDock?.Reset();
        _monitor = default; // displays may change between drags
        Transition(DragPhase.Idle);
    }

    /// <summary>
    /// Feeds a move to <see cref="EdgeDock"/>. The monitor's bounds are looked up when the pointer leaves the
    /// last one's; whether an edge has a monitor beyond it only when the pointer is against that edge.
    /// </summary>
    private void CheckEdge(System.Drawing.Point pt)
    {
        if (pt.X < _monitor.left || pt.X >= _monitor.right || pt.Y < _monitor.top || pt.Y >= _monitor.bottom)
        {
            var monitor = PInvoke.MonitorFromPoint(pt, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (!PInvoke.GetMonitorInfo(monitor, ref info))
            {
                return;
            }

            _monitor = info.rcMonitor;
            var dpi = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var x, out _).Succeeded ? x : 96u;
            _edgeZone = (int)Math.Ceiling(EdgeDock.ZoneDip * dpi / 96.0);
        }

        var bounds = _monitor;
        bool IsOpen(DockSide side) => PInvoke.MonitorFromPoint(new System.Drawing.Point(side == DockSide.Left ? bounds.left - 1 : bounds.right, pt.Y),
            MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL).IsNull;
        if (edgeDock!.OnMove(pt.X, bounds.left, bounds.right, _edgeZone, IsOpen) is { } reached)
        {
            EdgeReached?.Invoke(reached, pt.X, pt.Y);
        }
    }

    /// <summary><see cref="LongPress.SlopDip"/> in physical pixels on the monitor under <paramref name="pt"/>.</summary>
    private static int SlopAt(System.Drawing.Point pt)
    {
        var monitor = PInvoke.MonitorFromPoint(pt, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var dpi = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var x, out _).Succeeded ? x : 96u;
        return (int)Math.Round(LongPress.SlopDip * dpi / 96.0);
    }

    /// <summary>The primary button, which is the right one with the buttons swapped; GetAsyncKeyState reads the physical one.</summary>
    private static bool ButtonIsDown()
    {
        var button = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0 ? VIRTUAL_KEY.VK_RBUTTON : VIRTUAL_KEY.VK_LBUTTON;
        return (PInvoke.GetAsyncKeyState((int)button) & 0x8000) != 0;
    }

    /// <summary>
    /// Read from the key state rather than tracked here, so a key-up the hook never saw (Ctrl+Alt+Del takes
    /// the ups to the secure desktop) cannot leave a modifier held for good. Inside the hook the state
    /// still reflects the keys before this one.
    /// </summary>
    private static Modifiers HeldModifiers()
    {
        static bool Down(VIRTUAL_KEY key) => (PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0;
        return (Down(VIRTUAL_KEY.VK_SHIFT) ? Modifiers.Shift : Modifiers.None)
            | (Down(VIRTUAL_KEY.VK_CONTROL) ? Modifiers.Ctrl : Modifiers.None)
            | (Down(VIRTUAL_KEY.VK_MENU) ? Modifiers.Alt : Modifiers.None)
            | (Down(VIRTUAL_KEY.VK_LWIN) || Down(VIRTUAL_KEY.VK_RWIN) ? Modifiers.Win : Modifiers.None);
    }

    private void StopPolling()
    {
        if (_pollTimer != 0)
        {
            PInvoke.KillTimer(default, _pollTimer);
            _pollTimer = 0;
        }
    }

    private void StopHold()
    {
        if (_holdTimer != 0)
        {
            PInvoke.KillTimer(default, _holdTimer);
            _holdTimer = 0;
        }
    }

    private void Transition(DragPhase next)
    {
        if (_phase == next)
        {
            return;
        }

        _phase = next;
        PhaseChanged?.Invoke(next);
    }

    private static Modifiers ModifierOf(VIRTUAL_KEY vk) => vk switch
    {
        VIRTUAL_KEY.VK_SHIFT or VIRTUAL_KEY.VK_LSHIFT or VIRTUAL_KEY.VK_RSHIFT => Modifiers.Shift,
        VIRTUAL_KEY.VK_CONTROL or VIRTUAL_KEY.VK_LCONTROL or VIRTUAL_KEY.VK_RCONTROL => Modifiers.Ctrl,
        VIRTUAL_KEY.VK_MENU or VIRTUAL_KEY.VK_LMENU or VIRTUAL_KEY.VK_RMENU => Modifiers.Alt,
        VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN => Modifiers.Win,
        _ => Modifiers.None,
    };
}
