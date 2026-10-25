using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using ImageMagick;
using SkiaSharp;

namespace DropWheel.Engines.Pdf;

/// <summary>
/// Images to a PDF through Skia's PDF writer, one page per image (per frame for a TIFF, the first frame of a
/// GIF). The page is as large as the image at its own DPI, 96 when it has none, so nothing is resampled. A
/// JPEG's compressed data goes in unchanged, see <see cref="WithoutExif"/>. Other formats are decoded by Magick
/// and stored losslessly, except WebP and AVIF: PDF has no codec for them, and storing a photo losslessly would
/// make the PDF many times larger, so an opaque one is stored as JPEG.
/// </summary>
public static class ImagePdf
{
    internal static readonly string[] Inputs = ["jpg", "png", "webp", "avif", "gif", "bmp", "tiff"];

    private const double DefaultDpi = 96;

    /// <summary>Largest page side Acrobat opens, 200 inches; a larger image gets a higher DPI instead.</summary>
    private const double MaxPagePoints = 14400;

    private const int LossyQuality = 92;

    /// <summary>One image to one PDF.</summary>
    internal static Task<string?> WriteAsync(
        ConversionRequest request,
        string source,
        int index,
        DateTimeOffset now,
        IProgress<double> progress,
        CancellationToken cancellationToken) =>
        WriteAsync(request, [source], index, now, progress, cancellationToken);

    /// <summary>
    /// <paramref name="sources"/> to one PDF in that order, named after the first. Each image is loaded, drawn and
    /// let go before the next, so a long batch of photos does not have to fit in memory at once.
    /// </summary>
    /// <exception cref="InvalidDataException">An image cannot be read; the message names it.</exception>
    internal static async Task<string?> WriteAsync(
        ConversionRequest request,
        IReadOnlyList<string> sources,
        int index,
        DateTimeOffset now,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var metadata = SKDocumentPdfMetadata.Default with { Title = FormatRegistry.StemOf(sources[0]), Creator = "DropWheel" };

        // The first image is loaded up front: its size can be part of the name.
        var first = Load(sources[0]);
        try
        {
            var (width, height) = first[0].OrientedSize;
            return await OutputFiles.WriteAsync(request, sources[0], index, now, temp =>
            {
                using (var output = File.Create(temp))
                using (var document = SKDocument.CreatePdf(output, metadata))
                {
                    for (var i = 0; i < sources.Count; i++)
                    {
                        var pages = i == 0 ? first : Load(sources[i]);
                        try
                        {
                            for (var j = 0; j < pages.Count; j++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                pages[j].Draw(document);
                                progress.Report((i + ((j + 1.0) / pages.Count)) / sources.Count);
                            }
                        }
                        finally
                        {
                            if (i > 0)
                            {
                                Dispose(pages);
                            }
                        }
                    }

                    document.Close();
                }

                return Task.CompletedTask;
            }, width, height).ConfigureAwait(false);
        }
        finally
        {
            Dispose(first);
        }
    }

    private static List<Page> Load(string source)
    {
        var from = FormatRegistry.FromPath(source)?.Id;
        try
        {
            return from == "jpg" && Jpeg(source) is { } jpeg ? [jpeg] : Decode(source, from == "tiff", lossy: from is "webp" or "avif");
        }
        catch (MagickException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(source)} 无法读取：{ex.Message}", ex);
        }
    }

    private static void Dispose(List<Page> pages)
    {
        foreach (var page in pages)
        {
            page.Image.Dispose();
        }
    }

    /// <summary>The JPEG's own data, or null when Skia cannot decode it, so Magick gets a try.</summary>
    private static Page? Jpeg(string source)
    {
        // Read here rather than by Skia, which opens the path with the ANSI code page on Windows.
        var bytes = File.ReadAllBytes(source);
        SKEncodedOrigin origin;
        using (var codec = SKCodec.Create(new SKMemoryStream(bytes)))
        {
            if (codec is null)
            {
                return null;
            }

            origin = codec.EncodedOrigin;
        }

        using var data = SKData.CreateCopy(WithoutExif(bytes));
        return SKImage.FromEncodedData(data) is { } image ? new(image, origin, DpiOf(new MagickImageInfo(source).Density)) : null;
    }

    /// <summary>
    /// The JPEG without its EXIF segments. Skia passes a JPEG through only when it has no EXIF orientation,
    /// since its own decoder would turn the image; the page transform turns it instead. The camera's GPS
    /// position, also in EXIF, stays out of the PDF that way. Anything unexpected returns the JPEG as it is.
    /// </summary>
    public static byte[] WithoutExif(byte[] jpeg)
    {
        if (jpeg is not [0xFF, 0xD8, ..])
        {
            return jpeg;
        }

        using var output = new MemoryStream(jpeg.Length);
        output.Write(jpeg, 0, 2);
        var at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF)
        {
            var marker = jpeg[at + 1];
            if (marker == 0xFF)
            {
                at++; // fill byte
                continue;
            }

            if (marker == 0xDA)
            {
                // Start of scan: the rest is image data.
                output.Write(jpeg, at, jpeg.Length - at);
                return output.ToArray();
            }

            var end = at + 2 + ((jpeg[at + 2] << 8) | jpeg[at + 3]);
            if (end > jpeg.Length)
            {
                break;
            }

            if (!(marker == 0xE1 && jpeg.AsSpan(at + 4, end - at - 4).StartsWith("Exif\0\0"u8)))
            {
                output.Write(jpeg, at, end - at);
            }

            at = end;
        }

        return jpeg;
    }

    /// <param name="lossy">Store an opaque frame as JPEG.</param>
    private static List<Page> Decode(string source, bool allFrames, bool lossy)
    {
        using var frames = new MagickImageCollection();
        frames.Read(source, new MagickReadSettings { FrameCount = allFrames ? null : 1 });
        var pages = new List<Page>(frames.Count);
        foreach (var frame in frames)
        {
            frame.AutoOrient();
            var opaque = !frame.HasAlpha;
            using var pixels = frame.GetPixelsUnsafe();
            var rgba = pixels.ToByteArray(PixelMapping.RGBA) ?? throw new InvalidDataException($"无法读取 {Path.GetFileName(source)} 的像素");
            var info = new SKImageInfo((int)frame.Width, (int)frame.Height, SKColorType.Rgba8888, opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
            var image = SKImage.FromPixelCopy(info, rgba);
            if (lossy && opaque)
            {
                // Skia's writer passes encoded JPEG data through, so the quality is chosen here, per image.
                using var decoded = image;
                using var data = decoded.Encode(SKEncodedImageFormat.Jpeg, LossyQuality);
                image = SKImage.FromEncodedData(data);
            }

            pages.Add(new(image, SKEncodedOrigin.TopLeft, DpiOf(frame.Density)));
        }

        return pages;
    }

    private static double DpiOf(Density? density)
    {
        var dpi = density switch
        {
            { Units: DensityUnit.PixelsPerInch } d => d.X,
            { Units: DensityUnit.PixelsPerCentimeter } d => d.X * 2.54,
            _ => 0,
        };
        return dpi is >= 36 and <= 2400 ? dpi : DefaultDpi;
    }

    private sealed record Page(SKImage Image, SKEncodedOrigin Origin, double Dpi)
    {
        /// <summary>The last four EXIF orientations turn the image a quarter, so its width becomes the page height.</summary>
        private bool Turned => Origin >= SKEncodedOrigin.LeftTop;

        public (int Width, int Height) OrientedSize => Turned ? (Image.Height, Image.Width) : (Image.Width, Image.Height);

        public void Draw(SKDocument document)
        {
            var (width, height) = OrientedSize;
            var dpi = Math.Max(Dpi, Math.Max(width, height) * 72 / MaxPagePoints);

            // Skia rounds the page to whole points, so the image is stretched by that fraction to fill it.
            var pageWidth = Math.Max(1, Math.Floor(width * 72 / dpi + 0.5));
            var pageHeight = Math.Max(1, Math.Floor(height * 72 / dpi + 0.5));
            using var canvas = document.BeginPage((float)pageWidth, (float)pageHeight);
            canvas.Scale((float)(pageWidth / width), (float)(pageHeight / height));
            canvas.Concat(Orientation(Origin, Image.Width, Image.Height));
            canvas.DrawImage(Image, 0, 0, SKSamplingOptions.Default);
            document.EndPage();
        }

        /// <summary>Maps the stored pixels onto the upright image, as Skia's SkEncodedOriginToMatrix does.</summary>
        private static SKMatrix Orientation(SKEncodedOrigin origin, float w, float h) => origin switch
        {
            SKEncodedOrigin.TopRight => new(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
    }
}
