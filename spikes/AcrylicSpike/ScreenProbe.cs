using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace AcrylicSpike;

/// <summary>
/// BitBlt a fixed screen rectangle (physical pixels) into a 32-bpp DIB section and expose the BGRA
/// bytes.  Used as the "is it on screen yet" clock — XAML's Rendered event says a frame was produced,
/// not that DWM presented it — and to save screenshots for a human to look at.
/// </summary>
internal sealed unsafe class ScreenProbe : IDisposable
{
    private readonly HDC _screen;
    private readonly HDC _mem;
    private readonly HBITMAP _bitmap;
    private readonly HGDIOBJ _previous;
    private readonly byte* _bits;

    public ScreenProbe(int x, int y, int width, int height)
    {
        X = x; Y = y; Width = width; Height = height;
        _screen = PInvoke.GetDC(HWND.Null);
        _mem = PInvoke.CreateCompatibleDC(_screen);
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = (uint)BI_COMPRESSION.BI_RGB;
        void* bits;
        _bitmap = PInvoke.CreateDIBSection(_mem, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (_bitmap.IsNull)
        {
            throw new InvalidOperationException("CreateDIBSection failed");
        }

        _bits = (byte*)bits;
        _previous = PInvoke.SelectObject(_mem, _bitmap);
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    public byte[] Capture()
    {
        if (!PInvoke.BitBlt(_mem, 0, 0, Width, Height, _screen, X, Y, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT))
        {
            throw new InvalidOperationException("BitBlt failed");
        }

        PInvoke.GdiFlush();
        var copy = new byte[Width * Height * 4];
        new ReadOnlySpan<byte>(_bits, copy.Length).CopyTo(copy);
        return copy;
    }

    /// <summary>Fraction of pixels whose B, G or R differs by more than <paramref name="tolerance"/>.</summary>
    public static double DiffFraction(byte[] a, byte[] b, int tolerance = 10)
    {
        var n = a.Length / 4;
        var changed = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            if (Math.Abs(a[i] - b[i]) > tolerance || Math.Abs(a[i + 1] - b[i + 1]) > tolerance || Math.Abs(a[i + 2] - b[i + 2]) > tolerance)
            {
                changed++;
            }
        }

        return (double)changed / n;
    }

    /// <summary>
    /// Inside a centred disc of the given radius (pixels): mean and standard deviation of the grey level, the
    /// number of magenta (fallback colour) pixels, and the mean absolute horizontal grey gradient — a crude
    /// sharpness measure: hard checkerboard edges score high, blurred ones low.
    /// </summary>
    public static (double Mean, double StdDev, int Magenta, double Gradient) DiscStats(byte[] bgra, int width, int height, int radius)
    {
        double sum = 0, sumSq = 0, gradSum = 0;
        var n = 0;
        var gradN = 0;
        var magenta = 0;
        var cx = width / 2.0;
        var cy = height / 2.0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = x + 0.5 - cx;
                var dy = y + 0.5 - cy;
                if (dx * dx + dy * dy > radius * (double)radius)
                {
                    continue;
                }

                var i = (y * width + x) * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                var grey = (r + g + b) / 3.0;
                sum += grey; sumSq += grey * grey; n++;
                if (x + 1 < width)
                {
                    var j = i + 4;
                    gradSum += Math.Abs((bgra[j] + bgra[j + 1] + bgra[j + 2]) / 3.0 - grey);
                    gradN++;
                }
                if (r > 200 && b > 200 && g < 80)
                {
                    magenta++;
                }
            }
        }

        if (n == 0)
        {
            return (0, 0, 0, 0);
        }

        var mean = sum / n;
        return (mean, Math.Sqrt(Math.Max(0, sumSq / n - mean * mean)), magenta, gradN > 0 ? gradSum / gradN : 0);
    }

    public static void SaveBmp(string path, byte[] bgra, int width, int height)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        var imageBytes = width * height * 4;
        w.Write((ushort)0x4D42); w.Write(14 + 40 + imageBytes); w.Write(0); w.Write(14 + 40);
        w.Write(40); w.Write(width); w.Write(height); w.Write((ushort)1); w.Write((ushort)32); w.Write(0); w.Write(imageBytes); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        for (var row = height - 1; row >= 0; row--) // BMP is bottom-up
        {
            w.Write(bgra, row * width * 4, width * 4);
        }
    }

    public void Dispose()
    {
        PInvoke.SelectObject(_mem, _previous);
        PInvoke.DeleteObject(_bitmap);
        PInvoke.DeleteDC(_mem);
        _ = PInvoke.ReleaseDC(HWND.Null, _screen);
    }
}
