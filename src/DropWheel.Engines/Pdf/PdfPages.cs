// PDFtoImage marks its API with the platforms it ships PDFium for. DropWheel runs on Windows, its tests on macOS and Linux.
#pragma warning disable CA1416

using System.Globalization;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using PDFtoImage;
using SkiaSharp;

namespace DropWheel.Engines.Pdf;

/// <summary>
/// Renders every page of a PDF with PDFium, on white, with annotations and form fields. One page becomes one
/// image next to the PDF; more pages go into a folder named after it, as "report-01.png" and so on, numbered
/// with as many digits as the last page needs.
/// </summary>
internal static class PdfPages
{
    public const int Dpi = 150;

    /// <summary>Longest side of a rendered page in pixels; a poster-sized page is rendered at a lower DPI instead.</summary>
    private const int MaxSide = 10000;

    private const int JpegQuality = 90;

    public static async Task<string?> WriteAsync(
        ConversionRequest request,
        string source,
        int index,
        DateTimeOffset now,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var format = request.TargetFormatId == "jpg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
        using var input = File.OpenRead(source);
        var sizes = Conversion.GetPageSizes(input, leaveOpen: true);
        if (sizes.Count == 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(source)} 没有页面");
        }

        SKBitmap Render(int page)
        {
            var (width, height) = PixelSize(sizes[page]);
            input.Position = 0;
            return Conversion.ToImage(input, leaveOpen: true, page: page,
                options: new RenderOptions(Width: width, Height: height, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White));
        }

        if (sizes.Count == 1)
        {
            using var page = Render(0);
            return await OutputFiles.WriteAsync(request, source, index, now, temp =>
            {
                Save(page, temp, format);
                return Task.CompletedTask;
            }, page.Width, page.Height).ConfigureAwait(false);
        }

        var stem = FormatRegistry.StemOf(source);
        return await OutputFiles.WriteFolderAsync(request, source, index, now, folder =>
        {
            for (var number = 1; number <= sizes.Count; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var page = Render(number - 1);
                Save(page, Path.Combine(folder, FileName(stem, number, sizes.Count, request.TargetFormatId)), format);
                progress.Report((double)number / sizes.Count);
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>"report-07.pdf": the page number padded to as many digits as the last page needs, at least two.</summary>
    internal static string FileName(string stem, int number, int count, string extension)
    {
        var digits = Math.Max(2, count.ToString(CultureInfo.InvariantCulture).Length);
        return $"{stem}-{number.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')}.{extension}";
    }

    /// <summary>
    /// The page at <see cref="Dpi"/>, or smaller so its longest side stays within <see cref="MaxSide"/>. Sized here
    /// and rounded: PDFtoImage's own DPI sizing truncates, so a 240 pt page at 150 DPI came out 499 px wide, not 500.
    /// The sizes are those of the page as displayed, after its rotation.
    /// </summary>
    private static (int Width, int Height) PixelSize(System.Drawing.SizeF points)
    {
        var scale = Math.Min(Dpi / 72.0, MaxSide / Math.Max(points.Width, points.Height));
        return (Pixels(points.Width * scale), Pixels(points.Height * scale));
    }

    private static int Pixels(double size) => Math.Max(1, (int)Math.Round(size, MidpointRounding.AwayFromZero));

    /// <summary>The page is drawn on white, so it is saved as opaque: no alpha channel in the PNG.</summary>
    private static void Save(SKBitmap page, string path, SKEncodedImageFormat format)
    {
        using var pixels = new SKPixmap(page.Info.WithAlphaType(SKAlphaType.Opaque), page.GetPixels(), page.RowBytes);
        using var data = pixels.Encode(format, JpegQuality) ?? throw new InvalidDataException($"无法编码 {Path.GetFileName(path)}");
        using var output = File.Create(path);
        data.SaveTo(output);
    }
}
