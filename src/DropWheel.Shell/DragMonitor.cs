using DropWheel.Core.Input;
using DropWheel.Core.Settings;
using Windows.Win32;
using Windows.Win32.Foundation;
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
/// open the wheel. Win key events go through <see cref="WinKeyGate"/>.
///
/// Windows silently removes a low-level hook whose callback runs past LowLevelHooksTimeout, so the
/// callbacks only update state, and <see cref="PhaseChanged"/> and the tapper's Invoked are raised
/// inline on this thread: handlers must post to their own thread and return.
/// </summary>
public sealed unsafe class DragMonitor(ModifierTapper tapper, WinKeyGate winKeys) : IDisposable
{
    private const int ThresholdPx = 8;
    private const uint PollMs = 25;

    private Thread? _thread;
    private uint _threadId;
    private HOOKPROC? _mouseProc;
    private HOOKPROC? _keyboardProc;
    private nuint _pollTimer;
    private bool _buttonDown;
    private System.Drawing.Point _downAt;
    private long _pointer;
    private volatile DragPhase _phase;

    /// <summary>Raised on the hook thread, inside the hook callback.</summary>
    public event Action<DragPhase>? PhaseChanged;

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

            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }

        StopPolling();
    }

    private LRESULT MouseProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            try
            {
                var pt = ((MSLLHOOKSTRUCT*)lParam.Value)->pt;
                Volatile.Write(ref _pointer, ((long)pt.X << 32) | (uint)pt.Y);
                switch ((uint)wParam.Value)
                {
                    case PInvoke.WM_LBUTTONDOWN:
                        _buttonDown = true;
                        _downAt = pt;
                        StopPolling();
                        _pollTimer = PInvoke.SetTimer(default, 0, PollMs, null);
                        break;
                    case PInvoke.WM_MOUSEMOVE when _buttonDown && _phase == DragPhase.Idle
                        && (Math.Abs(pt.X - _downAt.X) > ThresholdPx || Math.Abs(pt.Y - _downAt.Y) > ThresholdPx):
                        Transition(DragPhase.MaybeDragging);
                        break;
                    case PInvoke.WM_LBUTTONUP:
                        _buttonDown = false;
                        StopPolling();
                        Transition(DragPhase.Idle);
                        break;
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
                    _buttonDown = false;
                    StopPolling();
                    Transition(DragPhase.Idle);
                }

                var dragging = _phase != DragPhase.Idle;
                var modifier = ModifierOf(vk);
                if (modifier == Modifiers.None)
                {
                    if (down)
                    {
                        tapper.OnOtherKey();
                    }
                }
                else
                {
                    tapper.OnModifier(modifier, down, dragging);
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

    /// <summary>The primary button, which is the right one with the buttons swapped; GetAsyncKeyState reads the physical one.</summary>
    private static bool ButtonIsDown()
    {
        var button = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0 ? VIRTUAL_KEY.VK_RBUTTON : VIRTUAL_KEY.VK_LBUTTON;
        return (PInvoke.GetAsyncKeyState((int)button) & 0x8000) != 0;
    }

    private void StopPolling()
    {
        if (_pollTimer != 0)
        {
            PInvoke.KillTimer(default, _pollTimer);
            _pollTimer = 0;
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
