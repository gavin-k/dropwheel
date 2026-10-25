using System.Runtime.InteropServices;
using DropWheel.Core.Wheel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.System.Ole;
using Windows.Win32.System.SystemServices;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DropWheel.Shell;

/// <summary>Where the wheel is on screen. Centre in physical pixels; <paramref name="Scale"/> is the monitor DPI / 96.</summary>
/// <param name="EnabledSectors">Six flags, sector 0 at 12 o'clock, clockwise (see <see cref="WheelGeometry"/>).</param>
/// <param name="StartsOnHub">False when the wheel was shifted away from the pointer; see <see cref="SectorGate"/>.</param>
public sealed record WheelPlacement(int CenterX, int CenterY, double Scale, IReadOnlyList<bool> EnabledSectors, bool StartsOnHub = true)
{
    /// <summary>The enabled sector under a screen point, or -1; also -1 while <paramref name="gate"/> is shut.</summary>
    public int SectorAt(int x, int y, SectorGate gate, out bool opened)
    {
        var sector = gate.SectorAt((x - CenterX) / Scale, (y - CenterY) / Scale, out opened);
        return sector >= 0 && sector < EnabledSectors.Count && EnabledSectors[sector] ? sector : -1;
    }
}

/// <summary>
/// The OLE drop target behind the wheel (ADR-0004). A layered, top-most, never-activated popup that
/// covers the whole virtual screen at alpha 1 (alpha 0 is not hit-tested), created once on its own STA
/// thread and registered with RegisterDragDrop once; the wheel only shows and hides it. Covering the
/// whole screen means a release anywhere lands here, so a release off the wheel is a cancel rather
/// than a drop into whatever window happens to be underneath.
///
/// Events are raised on the host thread from inside the OLE callbacks: keep handlers short and
/// marshal to the UI thread.
/// </summary>
public sealed unsafe class DropTargetHost : IDisposable
{
    private const uint ShowMessage = PInvoke.WM_APP + 1;
    private const uint HideMessage = PInvoke.WM_APP + 2;
    private const string ClassName = "DropWheel.DropTargetHost";

    private readonly Target _target;
    private Thread? _thread;
    private HWND _hwnd;
    private WNDPROC? _wndProc;
    private volatile WheelPlacement? _placement;

    public DropTargetHost() => _target = new Target(this);

    /// <summary>A drag entered while the host was shown. Paths are empty when the data has no CF_HDROP.</summary>
    public event Action<IReadOnlyList<string>>? Entered;

    /// <summary>The hovered enabled sector changed; -1 means none.</summary>
    public event Action<int>? Hovered;

    /// <summary>The pointer reached the hub of a wheel that opened off-centre, so sectors respond from now on.</summary>
    public event Action? HubReached;

    /// <summary>Files were released on an enabled sector.</summary>
    public event Action<IReadOnlyList<string>, int>? Dropped;

    /// <summary>The drag left without dropping on a sector: released elsewhere, Esc, or the source gave up.</summary>
    public event Action? Canceled;

    public bool IsVisible => _placement is not null;

    /// <summary>Creates the window and registers it as a drop target. Blocks until done; throws if registration fails.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready, ref failure)) { IsBackground = true, Name = "DropTargetHost" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (failure is not null)
        {
            _thread = null;
            throw failure;
        }
    }

    /// <summary>Show the host over the whole virtual screen. Call after the wheel is visible.</summary>
    public void Show(WheelPlacement placement)
    {
        _placement = placement;
        PInvoke.PostMessage(_hwnd, ShowMessage, default, default);
    }

    public void Hide()
    {
        _placement = null;
        PInvoke.PostMessage(_hwnd, HideMessage, default, default);
    }

    public void Dispose()
    {
        if (_thread is null)
        {
            return;
        }

        PInvoke.PostMessage(_hwnd, PInvoke.WM_DESTROY, default, default);
        _thread.Join(2000);
        _thread = null;
    }

    private void Run(ManualResetEventSlim ready, ref Exception? failure)
    {
        try
        {
            var hr = PInvoke.OleInitialize(null);
            Marshal.ThrowExceptionForHR(hr);
            _hwnd = CreateWindow();
            hr = PInvoke.RegisterDragDrop(_hwnd, _target);
            Marshal.ThrowExceptionForHR(hr);
        }
        catch (Exception ex)
        {
            failure = ex;
            ready.Set();
            return;
        }

        ready.Set();
        while (PInvoke.GetMessage(out var msg, default, 0, 0))
        {
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }

        PInvoke.OleUninitialize();
    }

    private HWND CreateWindow()
    {
        _wndProc = WndProc;
        using var module = PInvoke.GetModuleHandle((string?)null);
        fixed (char* className = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = (HINSTANCE)module.DangerousGetHandle(),
                hbrBackground = (HBRUSH)PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.BLACK_BRUSH).Value,
                lpszClassName = className,
            };
            if (PInvoke.RegisterClassEx(in wc) == 0)
            {
                throw new System.ComponentModel.Win32Exception();
            }
        }

        var hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
            ClassName,
            "DropWheel drop target",
            WINDOW_STYLE.WS_POPUP,
            0, 0, 1, 1,
            default, null, module, null);
        if (hwnd.IsNull)
        {
            throw new System.ComponentModel.Win32Exception();
        }

        // Alpha 1 of 255 over black: invisible, but still hit-tested (alpha 0 is not).
        PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 1, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        return hwnd;
    }

    private LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case ShowMessage:
                PInvoke.SetWindowPos(
                    hwnd,
                    HWND.HWND_TOPMOST,
                    PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
                    PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
                    PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
                    PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN),
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
                return default;
            case HideMessage:
                PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_HIDE);
                _target.Reset();
                return default;
            case PInvoke.WM_DESTROY:
                PInvoke.RevokeDragDrop(hwnd);
                PInvoke.DestroyWindow(hwnd);
                PInvoke.PostQuitMessage(0);
                return default;
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static List<string> FilePaths(IDataObject data)
    {
        var format = new FORMATETC
        {
            cfFormat = (ushort)CLIPBOARD_FORMAT.CF_HDROP,
            dwAspect = (uint)DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = (uint)TYMED.TYMED_HGLOBAL,
        };
        try
        {
            data.GetData(&format, out var medium);
            try
            {
                var drop = (HDROP)medium.u.hGlobal.Value;
                var count = PInvoke.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
                var paths = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    var length = PInvoke.DragQueryFile(drop, i, null, 0);
                    var buffer = new char[length + 1];
                    fixed (char* p = buffer)
                    {
                        length = PInvoke.DragQueryFile(drop, i, p, length + 1);
                    }

                    paths.Add(new string(buffer, 0, (int)length));
                }

                return paths;
            }
            finally
            {
                PInvoke.ReleaseStgMedium(ref medium);
            }
        }
        catch (COMException)
        {
            return [];
        }
    }

    /// <summary>The IDropTarget OLE calls. Runs on the host thread.</summary>
    private sealed class Target(DropTargetHost host) : IDropTarget
    {
        private List<string> _paths = [];
        private int _hovered = -1;
        private bool _inside;
        private SectorGate? _gate;

        /// <summary>Forget the drag. Runs when the host hides, so the next showing gets a fresh gate.</summary>
        public void Reset()
        {
            _paths = [];
            _hovered = -1;
            _inside = false;
            _gate = null;
        }

        public void DragEnter(IDataObject pDataObj, MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect)
        {
            _paths = FilePaths(pDataObj);
            _inside = true;
            _hovered = -1;
            host.Entered?.Invoke(_paths);
            *pdwEffect = Effect(pt, *pdwEffect);
        }

        public void DragOver(MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect) =>
            *pdwEffect = Effect(pt, *pdwEffect);

        public void DragLeave()
        {
            if (_inside)
            {
                Reset();
                host.Canceled?.Invoke();
            }
        }

        public void Drop(IDataObject pDataObj, MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect)
        {
            *pdwEffect = Effect(pt, *pdwEffect);
            var sector = _hovered;
            var paths = _paths.Count > 0 ? _paths : FilePaths(pDataObj);
            Reset();
            if (*pdwEffect == DROPEFFECT.DROPEFFECT_COPY && paths.Count > 0)
            {
                host.Dropped?.Invoke(paths, sector);
            }
            else
            {
                *pdwEffect = DROPEFFECT.DROPEFFECT_NONE;
                host.Canceled?.Invoke();
            }
        }

        /// <summary>
        /// Copy over an enabled sector when the source allows it, otherwise none. Never Move: the
        /// source deletes the original after a Move.
        /// </summary>
        private DROPEFFECT Effect(POINTL pt, DROPEFFECT allowed)
        {
            var sector = -1;
            if (host._placement is { } placement)
            {
                // One gate per showing: Show is called again with the enabled sectors once the files are
                // known, and that must not shut a gate the pointer already opened.
                _gate ??= new SectorGate(placement.StartsOnHub);
                sector = placement.SectorAt(pt.x, pt.y, _gate, out var opened);
                if (opened)
                {
                    host.HubReached?.Invoke();
                }
            }

            if (sector != _hovered)
            {
                _hovered = sector;
                host.Hovered?.Invoke(sector);
            }

            return sector >= 0 && _paths.Count > 0 && (allowed & DROPEFFECT.DROPEFFECT_COPY) != 0
                ? DROPEFFECT.DROPEFFECT_COPY
                : DROPEFFECT.DROPEFFECT_NONE;
        }
    }
}
