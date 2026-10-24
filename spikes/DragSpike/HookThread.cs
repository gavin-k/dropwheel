using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DragSpike;

/// <summary>
/// Dedicated thread owning WH_MOUSE_LL, WH_KEYBOARD_LL and a WinEvent hook for
/// EVENT_SYSTEM_DRAGDROPSTART/END, plus a 25 ms poll for a "SysDragImage" top-level window
/// while the left button is down.  Hook procedures only record timestamps and raise events;
/// nothing slow runs on this thread so Windows never times the hooks out.
/// </summary>
internal sealed unsafe class HookThread : IDisposable
{
    private const int DragThresholdPx = 8;
    private const double TapWindowMs = 300;

    private Thread? _thread;
    private uint _threadId;
    private HOOKPROC? _mouseProc;
    private HOOKPROC? _keyboardProc;
    private WINEVENTPROC? _winEventProc;
    private FreeLibrarySafeHandle? _user32;
    private UnhookWindowsHookExSafeHandle? _mouseHook;
    private UnhookWindowsHookExSafeHandle? _keyboardHook;
    private HWINEVENTHOOK _winEventHook;
    private System.Threading.Timer? _dragImagePoll;

    private double _shiftDownAt = -1;
    private bool _otherKeyWhileShift;

    /// <summary>§7 #4: when set, Win key down/up are swallowed (not passed to the next hook) while dragging.</summary>
    public volatile bool SwallowWinKeyWhileDragging;
    public int WinKeysSwallowed;

    // --- observed state (written on hook thread, read anywhere) ---------------------------------
    public volatile bool MouseDown;
    public volatile bool MovedBeyondThreshold;
    public Point MouseDownAt;
    public Point LastPointer;

    public double? TMouseDown;
    public double? TMoveThreshold;
    public double? TWinEventStart;
    public double? TWinEventEnd;
    public double? TSysDragImage;
    public string? WinEventStartClass;

    /// <summary>Raised on the hook thread when Shift was tapped (down+up within the window, no other key) while dragging.</summary>
    public event Action<Point, double>? ShiftTapped;

    /// <summary>Best-effort "a drag is in progress": any of the three signals.</summary>
    public bool Dragging =>
        (TWinEventStart is { } s && !(TWinEventEnd is { } e && e > s))
        || (TSysDragImage.HasValue && MouseDown)
        || (MouseDown && MovedBeyondThreshold);

    public string DragSignals =>
        $"winevent={(TWinEventStart.HasValue ? "yes" : "no")} sysdragimage={(TSysDragImage.HasValue ? "yes" : "no")} llmouse={(MovedBeyondThreshold ? "yes" : "no")}";

    public void Start()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "hooks" };
        _thread.Start();
        ready.Wait();
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = PInvoke.GetCurrentThreadId();
        _mouseProc = MouseProc;
        _keyboardProc = KeyboardProc;
        _winEventProc = WinEventProc;

        _user32 = PInvoke.LoadLibrary("user32.dll");
        _mouseHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, _mouseProc, _user32, 0);
        var mouseErr = Marshal.GetLastWin32Error();
        _keyboardHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _keyboardProc, _user32, 0);
        var kbdErr = Marshal.GetLastWin32Error();
        _winEventHook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_DRAGDROPSTART, PInvoke.EVENT_SYSTEM_DRAGDROPEND,
            new HMODULE(0), _winEventProc, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT);
        var weErr = Marshal.GetLastWin32Error();

        Log.Info($"hooks installed: mouseLL={!_mouseHook.IsInvalid} (err {mouseErr}) keyboardLL={!_keyboardHook.IsInvalid} (err {kbdErr}) winevent={!_winEventHook.IsNull} (err {weErr})");

        _dragImagePoll = new System.Threading.Timer(PollDragImage, null, 0, 25);
        ready.Set();

        while (PInvoke.GetMessage(out var msg, new HWND(0), 0, 0))
        {
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }
    }

    private unsafe LRESULT MouseProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            var info = (MSLLHOOKSTRUCT*)lParam.Value;
            var pt = info->pt;
            LastPointer = pt;
            switch ((uint)wParam.Value)
            {
                case PInvoke.WM_LBUTTONDOWN:
                    MouseDown = true;
                    MouseDownAt = pt;
                    MovedBeyondThreshold = false;
                    TMouseDown = Log.NowMs;
                    TMoveThreshold = null;
                    TSysDragImage = null;
                    Log.Info($"LL mouse: left down at {pt.X},{pt.Y}");
                    break;
                case PInvoke.WM_LBUTTONUP:
                    MouseDown = false;
                    Log.Info($"LL mouse: left up at {pt.X},{pt.Y}");
                    break;
                case PInvoke.WM_MOUSEMOVE:
                    if (MouseDown && !MovedBeyondThreshold
                        && (Math.Abs(pt.X - MouseDownAt.X) > DragThresholdPx || Math.Abs(pt.Y - MouseDownAt.Y) > DragThresholdPx))
                    {
                        MovedBeyondThreshold = true;
                        TMoveThreshold = Log.NowMs;
                        Log.Info($"LL mouse: moved past drag threshold at {pt.X},{pt.Y}");
                    }
                    break;
            }
        }

        return PInvoke.CallNextHookEx(new HHOOK(0), code, wParam, lParam);
    }

    private unsafe LRESULT KeyboardProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            var info = (KBDLLHOOKSTRUCT*)lParam.Value;
            var msg = (uint)wParam.Value;
            var vk = info->vkCode;
            var down = msg is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
            var isShift = vk is 0x10 or 0xA0 or 0xA1;
            var injected = (info->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) != 0;
            var now = Log.NowMs;
            var queueLatency = (int)(PInvoke.GetTickCount() - info->time);

            if (isShift)
            {
                if (down)
                {
                    if (_shiftDownAt < 0)
                    {
                        _shiftDownAt = now;
                        _otherKeyWhileShift = false;
                        Log.Info($"LL key: Shift down (dragging={Dragging}, injected={injected}, tick-latency={queueLatency} ms)");
                    }
                }
                else
                {
                    var held = now - _shiftDownAt;
                    var dragging = Dragging;
                    Log.Info($"LL key: Shift up after {held:F0} ms (dragging={dragging}, otherKey={_otherKeyWhileShift}, tick-latency={queueLatency} ms)");
                    if (_shiftDownAt >= 0 && held <= TapWindowMs && !_otherKeyWhileShift && dragging)
                    {
                        ShiftTapped?.Invoke(LastPointer, now);
                    }

                    _shiftDownAt = -1;
                }
            }
            else if (down && _shiftDownAt >= 0)
            {
                _otherKeyWhileShift = true;
            }
            else if (vk is 0x5B or 0x5C)
            {
                var swallow = SwallowWinKeyWhileDragging && Dragging;
                Log.Info($"LL key: Win {(down ? "down" : "up")} (dragging={Dragging}, swallow={swallow})");
                if (swallow)
                {
                    WinKeysSwallowed++;
                    return new LRESULT(1);
                }
            }
        }

        return PInvoke.CallNextHookEx(new HHOOK(0), code, wParam, lParam);
    }

    private void WinEventProc(HWINEVENTHOOK hook, uint evt, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == PInvoke.EVENT_SYSTEM_DRAGDROPSTART)
        {
            TWinEventStart = Log.NowMs;
            TWinEventEnd = null;
            WinEventStartClass = ClassName(hwnd);
            Log.Info($"WinEvent: DRAGDROPSTART hwnd=0x{(nint)hwnd.Value:X} class={WinEventStartClass}");
        }
        else if (evt == PInvoke.EVENT_SYSTEM_DRAGDROPEND)
        {
            TWinEventEnd = Log.NowMs;
            Log.Info($"WinEvent: DRAGDROPEND hwnd=0x{(nint)hwnd.Value:X} class={ClassName(hwnd)}");
        }
    }

    private void PollDragImage(object? _)
    {
        if (!MouseDown || TSysDragImage.HasValue)
        {
            return;
        }

        var h = PInvoke.FindWindowEx(new HWND(0), new HWND(0), "SysDragImage", null);
        if (!h.IsNull)
        {
            TSysDragImage = Log.NowMs;
            Log.Info($"SysDragImage: window appeared hwnd=0x{(nint)h.Value:X}");
        }
    }

    public static string ClassName(HWND hwnd)
    {
        Span<char> buf = stackalloc char[128];
        var n = PInvoke.GetClassName(hwnd, buf);
        return n > 0 ? new string(buf[..n]) : "?";
    }

    public void Dispose()
    {
        _dragImagePoll?.Dispose();
        if (_threadId != 0)
        {
            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, new WPARAM(0), new LPARAM(0));
            _thread?.Join(2000);
        }

        _mouseHook?.Dispose();
        _keyboardHook?.Dispose();
        if (!_winEventHook.IsNull) { PInvoke.UnhookWinEvent(_winEventHook); }
    }
}
