using DropWheel.Core.Wheel;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace DropWheel.App.Overlay;

/// <summary>
/// Copies a rectangle of the screen (physical pixels, layered windows included) with GDI, as the acrylic
/// spike measured it. GDI is what still works when D3D presents white, so it sees what the user sees.
/// Any thread.
/// </summary>
internal static unsafe class ScreenCopy
{
    /// <returns>The verdict on the copy, or null when GDI would not copy the screen.</returns>
    public static FrameVerdict? Judge(RectInt32 area)
    {
        HDC screen = PInvoke.GetDC(HWND.Null);
        HDC memory = PInvoke.CreateCompatibleDC(screen);
        var info = new BITMAPINFO();
        info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = area.Width;
        info.bmiHeader.biHeight = -area.Height; // top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = (uint)BI_COMPRESSION.BI_RGB;
        void* bits;
        HBITMAP bitmap = PInvoke.CreateDIBSection(memory, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        try
        {
            if (bitmap.IsNull)
            {
                return null;
            }

            HGDIOBJ previous = PInvoke.SelectObject(memory, bitmap);
            var copied = PInvoke.BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.X, area.Y, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT);
            PInvoke.GdiFlush();
            PInvoke.SelectObject(memory, previous);
            return copied ? FrameCheck.Judge(new ReadOnlySpan<byte>(bits, area.Width * area.Height * 4), area.Width, area.Height) : null;
        }
        finally
        {
            if (!bitmap.IsNull)
            {
                PInvoke.DeleteObject(bitmap);
            }

            PInvoke.DeleteDC(memory);
            _ = PInvoke.ReleaseDC(HWND.Null, screen);
        }
    }
}
