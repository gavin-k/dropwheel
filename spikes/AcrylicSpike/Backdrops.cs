using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.Shell;

namespace AcrylicSpike;

/// <summary>
/// Window-level backdrop that paints nothing, which is what makes a WinUI 3 window see-through:
/// the system-backdrop sprite sits behind the whole XAML tree, so a fully transparent brush there
/// (plus a null root background) leaves only our own elements on screen — *provided* DWM is told to treat
/// the HWND as per-pixel alpha and the HWND's own GDI surface stays 0x00000000 (same recipe as WinUIEx's
/// TransparentTintBackdrop; the brush alone leaves the window opaque white, see README).
///
/// ICompositionSupportsSystemBackdrop.SystemBackdrop is still typed as the *system*
/// Windows.UI.Composition.CompositionBrush (not Microsoft.UI.Composition), so the brush has to come from
/// a Windows.UI.Composition.Compositor, which in turn needs a Windows.System.DispatcherQueue on this
/// thread — WinUI only provides the Microsoft.UI.Dispatching one.  Same workaround as WinUIEx.
/// </summary>
internal sealed unsafe class TransparentBackdrop : SystemBackdrop
{
    private const uint DwmBbEnable = 1, DwmBbBlurRegion = 2;
    private static readonly SUBCLASSPROC SubclassProc = OnMessage; // keep the delegate alive for the lifetime of the window
    private static HBRUSH _black;
    private HWND _hwnd;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _hwnd = new HWND((void*)(nint)xamlRoot.ContentIslandEnvironment.AppWindowId.Value);

        // 1. DWM: no frame, and a blur-behind region that is empty → DWM treats the HWND surface as per-pixel alpha.
        ConfigureDwm(_hwnd);
        // 2. transparent system-backdrop brush behind the XAML tree
        connectedTarget.SystemBackdrop = SystemCompositor.Instance.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
        // 3. keep the HWND's own (GDI) surface at 0x00000000 instead of the class background → stays transparent
        var ok = PInvoke.SetWindowSubclass(_hwnd, SubclassProc, 1, 0);
        var hdc = PInvoke.GetDC(_hwnd);
        ClearBackground(_hwnd, hdc);
        PInvoke.ReleaseDC(_hwnd, hdc);
        Log.Info($"transparent backdrop connected to the window (DWM configured, subclass={ok})");
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
        if (!_hwnd.IsNull)
        {
            PInvoke.RemoveWindowSubclass(_hwnd, SubclassProc, 1);
        }
    }

    private static void ConfigureDwm(HWND hwnd)
    {
        var margins = new MARGINS();
        var hr1 = PInvoke.DwmExtendFrameIntoClientArea(hwnd, &margins);
        var bb = new DWM_BLURBEHIND { dwFlags = DwmBbEnable | DwmBbBlurRegion, fEnable = true, hRgnBlur = PInvoke.CreateRectRgn(-2, -2, -1, -1) };
        var hr2 = PInvoke.DwmEnableBlurBehindWindow(hwnd, &bb);
        // Win11 otherwise paints a 1 px rounded border around the (invisible) window rectangle.
        var corner = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        var hr3 = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        var none = 0xFFFFFFFE; // DWMWA_COLOR_NONE
        var hr4 = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, &none, sizeof(uint));
        Log.Info($"DwmExtendFrameIntoClientArea hr=0x{hr1.Value:X8}, DwmEnableBlurBehindWindow(empty region) hr=0x{hr2.Value:X8}, corner=DONOTROUND hr=0x{hr3.Value:X8}, border=NONE hr=0x{hr4.Value:X8}");
    }

    private static void ClearBackground(HWND hwnd, HDC hdc)
    {
        if (PInvoke.GetClientRect(hwnd, out var rect))
        {
            if (_black.IsNull)
            {
                _black = PInvoke.CreateSolidBrush(new COLORREF(0));
            }

            PInvoke.FillRect(hdc, &rect, _black);
        }
    }

    private static LRESULT OnMessage(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam, nuint id, nuint refData)
    {
        switch (msg)
        {
            case PInvoke.WM_ERASEBKGND:
                ClearBackground(hwnd, new HDC((nint)wParam.Value));
                return new LRESULT(1);
            case PInvoke.WM_DWMCOMPOSITIONCHANGED:
                ConfigureDwm(hwnd);
                return new LRESULT(0);
        }

        return PInvoke.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}

/// <summary>
/// Desktop acrylic for the SystemBackdropElement disc, through our own DesktopAcrylicController so we
/// can read <see cref="DesktopAcrylicController.State"/> (Active / Fallback) and paint the fallback
/// magenta — a solid fallback is then unmistakable in the screenshots.
///
/// By default the configuration says IsInputActive = false for a window that is never activated,
/// and acrylic renders its fallback colour for inactive windows.  The overlay is never active by
/// design, so unless <paramref name="useDefaultConfiguration"/> is set we pin IsInputActive = true.
/// </summary>
internal sealed class AcrylicDiscBackdrop(bool useDefaultConfiguration) : SystemBackdrop
{
    public DesktopAcrylicController? Controller { get; private set; }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (Controller is not null)
        {
            throw new InvalidOperationException("one disc per backdrop in this spike");
        }

        Controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Base,
            FallbackColor = Color.FromArgb(255, 255, 0, 255),
            TintColor = Color.FromArgb(255, 32, 32, 32),
            TintOpacity = 0.5f,
            LuminosityOpacity = 0.75f,
        };
        Controller.StateChanged += (s, _) => Log.Info($"acrylic state changed -> {s.State}");
        Controller.SetSystemBackdropConfiguration(Configuration(connectedTarget, xamlRoot));
        Controller.AddSystemBackdropTarget(connectedTarget);
        Log.Info($"acrylic connected to the disc: state={Controller.State}, defaultConfig={useDefaultConfiguration}, supported={DesktopAcrylicController.IsSupported()}");
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        Controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        Controller?.Dispose();
        Controller = null;
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        var cfg = Configuration(target, xamlRoot);
        Log.Info($"default configuration changed: IsInputActive={GetDefaultSystemBackdropConfiguration(target, xamlRoot).IsInputActive} -> applying IsInputActive={cfg.IsInputActive}");
        Controller?.SetSystemBackdropConfiguration(cfg);
    }

    private SystemBackdropConfiguration Configuration(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        var def = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        if (useDefaultConfiguration)
        {
            return def;
        }

        return new SystemBackdropConfiguration
        {
            IsInputActive = true,
            IsHighContrast = def.IsHighContrast,
            Theme = SystemBackdropTheme.Dark,
        };
    }
}

/// <summary>Lazily creates a Windows.System dispatcher queue for the UI thread and a system compositor on it.</summary>
internal static class SystemCompositor
{
    private static Windows.UI.Composition.Compositor? _compositor;
    private static nint _controller;

    public static Windows.UI.Composition.Compositor Instance
    {
        get
        {
            if (_compositor is null)
            {
                if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
                {
                    var options = new DispatcherQueueOptions { dwSize = 12, threadType = 2 /* current */, apartmentType = 2 /* STA */ };
                    var hr = CreateDispatcherQueueController(options, out _controller);
                    Log.Info($"CreateDispatcherQueueController hr=0x{hr:X8}");
                }

                _compositor = new Windows.UI.Composition.Compositor();
            }

            return _compositor;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [System.Runtime.InteropServices.DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint dispatcherQueueController);
}
