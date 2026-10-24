using DropWheel.App.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DropWheel.App.Overlay;

/// <summary>
/// Window backdrop that paints nothing, which makes a WinUI 3 window see-through (spikes/AcrylicSpike,
/// finding 1; same recipe as WinUIEx's TransparentTintBackdrop). A transparent system-backdrop brush is
/// not enough on its own: DWM also has to treat the HWND as per-pixel alpha (empty blur-behind region)
/// and the HWND's own GDI surface has to stay 0x00000000 instead of the class background.
/// The client area also has to cover the whole window: the borderless OverlappedPresenter keeps
/// WS_DLGFRAME, and DWM paints that 3 px frame as an opaque white rectangle around the wheel.
/// </summary>
internal sealed unsafe class TransparentBackdrop : SystemBackdrop
{
    private const uint DwmBbEnable = 1;
    private const uint DwmBbBlurRegion = 2;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private static readonly SUBCLASSPROC SubclassProc = OnMessage; // must outlive the window
    private static HBRUSH _black;
    private HWND _hwnd;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _hwnd = new HWND((void*)(nint)xamlRoot.ContentIslandEnvironment.AppWindowId.Value);
        ConfigureDwm(_hwnd);
        connectedTarget.SystemBackdrop = SystemCompositor.Instance.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
        PInvoke.SetWindowSubclass(_hwnd, SubclassProc, 1, 0);
        _ = PInvoke.SetWindowPos(_hwnd, HWND.Null, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        var hdc = PInvoke.GetDC(_hwnd);
        ClearBackground(_hwnd, hdc);
        _ = PInvoke.ReleaseDC(_hwnd, hdc);
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
        var margins = default(MARGINS);
        var extend = PInvoke.DwmExtendFrameIntoClientArea(hwnd, &margins);
        var blur = new DWM_BLURBEHIND { dwFlags = DwmBbEnable | DwmBbBlurRegion, fEnable = true, hRgnBlur = PInvoke.CreateRectRgn(-2, -2, -1, -1) };
        var blurBehind = PInvoke.DwmEnableBlurBehindWindow(hwnd, &blur);

        // Win11 otherwise draws a 1 px rounded border around the invisible window rectangle.
        var corner = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        _ = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        var border = DwmColorNone;
        _ = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, &border, sizeof(uint));
        if (extend.Failed || blurBehind.Failed)
        {
            FileLog.Error($"overlay: DWM transparency setup failed (extend 0x{extend.Value:X8}, blur-behind 0x{blurBehind.Value:X8})");
        }
    }

    private static void ClearBackground(HWND hwnd, HDC hdc)
    {
        if (PInvoke.GetClientRect(hwnd, out var rect))
        {
            if (_black.IsNull)
            {
                _black = PInvoke.CreateSolidBrush(new COLORREF(0));
            }

            _ = PInvoke.FillRect(hdc, &rect, _black);
        }
    }

    private static LRESULT OnMessage(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam, nuint id, nuint refData)
    {
        switch (msg)
        {
            case PInvoke.WM_ERASEBKGND:
                ClearBackground(hwnd, new HDC((nint)wParam.Value));
                return new LRESULT(1);
            case PInvoke.WM_NCCALCSIZE when wParam.Value != 0:
                // No non-client frame: the client rectangle stays the window rectangle.
                return new LRESULT(0);
            case PInvoke.WM_DWMCOMPOSITIONCHANGED:
                ConfigureDwm(hwnd);
                return new LRESULT(0);
        }

        return PInvoke.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}
