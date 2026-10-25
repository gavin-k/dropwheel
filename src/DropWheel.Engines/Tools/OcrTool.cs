// PDFtoImage marks its API with the platforms it ships PDFium for. DropWheel runs on Windows, its tests on macOS and Linux.
#pragma warning disable CA1416

using System.Text;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Image;
using DropWheel.Engines.Ocr;
using DropWheel.Engines.Pdf;
using ImageMagick;
using PDFtoImage;
using SkiaSharp;

namespace DropWheel.Engines.Tools;

/// <summary>
/// 识别文字: the text in each image, or on every page of a PDF, read by <paramref name="recognizer"/> and saved
/// next to it as "{name}.txt", UTF-8 with a BOM and CRLF like a PDF's text layer, a blank line between pages.
/// Words get a space between them unless both sides are Chinese, Japanese or Korean. A source with no text
/// gets no file; when none has any, the job is skipped. Images are turned upright, put on white and scaled
/// down to what the recognizer takes; PDF pages are rendered at <see cref="PdfDpi"/>.
/// </summary>
public sealed class OcrTool(ITextRecognizer recognizer, TimeProvider? time = null) : IWheelOperation
{
    /// <summary>Enough for print-sized text; a page larger than the recognizer takes is rendered smaller.</summary>
    internal const int PdfDpi = 300;

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "tool.ocr";

    public WheelMode Mode => WheelMode.Tools;

    public string Label => "识别文字";

    public string Hint => "存成 TXT";

    public bool AppliesTo(MediaKind kind) => kind is MediaKind.Image or MediaKind.Pdf;

    public Availability Check(ConversionRequest request)
    {
        if (recognizer.Unavailable is { } reason)
        {
            return Availability.Unavailable(reason) with { Cause = UnavailableCause.MissingOptionalModule };
        }

        foreach (var path in request.SourcePaths)
        {
            var format = FormatRegistry.FromPath(path);
            if (format is null || (format.Id != "pdf" && !MagickImageConverter.Reads(format.Id)))
            {
                return Availability.Unavailable($"不支持 {format?.DisplayName ?? Path.GetFileName(path)}");
            }

            if (!File.Exists(path))
            {
                return Availability.Unavailable($"找不到 {Path.GetFileName(path)}");
            }
        }

        return Availability.Available;
    }

    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var now = _time.GetLocalNow();
        var plan = request with { TargetFormatId = "txt", Naming = NamingTemplate.Default };
        int lines = 0, empty = 0;
        try
        {
            var written = await OutputFiles.ForEachSourceAsync(request, progress, (source, i, within) => Task.Run(async () =>
            {
                var text = await ReadAsync(source, within, cancellationToken).ConfigureAwait(false);
                if (text.Count == 0)
                {
                    empty++;
                    return null;
                }

                lines += text.Count(line => line.Length > 0);
                var all = string.Join("\r\n", text) + "\r\n";
                return await OutputFiles.WriteAsync(plan, source, i, now, temp => File.WriteAllTextAsync(temp, all, Utf8WithBom, cancellationToken)).ConfigureAwait(false);
            }, cancellationToken), cancellationToken).ConfigureAwait(false);

            if (empty == request.SourcePaths.Count)
            {
                return new(true, []) { Skipped = true, Detail = "没有认出文字" };
            }

            var notes = new[] { $"认出 {lines} 行，识别语言 {recognizer.Language}", empty > 0 ? $"另有 {empty} 个没有认出文字" : null };
            return ConversionResult.Written(written) with { Detail = string.Join("；", notes.OfType<string>()) };
        }
        catch (InvalidDataException ex)
        {
            return new(false, [], ex.Message);
        }
    }

    /// <summary>The lines of <paramref name="source"/>, pages apart by a blank line; none when it has no text.</summary>
    private async Task<List<string>> ReadAsync(string source, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(source);
        try
        {
            return FormatRegistry.FromPath(source)?.Id == "pdf"
                ? await ReadPagesAsync(source, progress, cancellationToken).ConfigureAwait(false)
                : await ReadImageAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{name} 无法识别：{ex.Message}", ex);
        }
        catch (Exception ex) when (PdfConverter.Unreadable(ex, name) is { } message)
        {
            throw new InvalidDataException(message, ex);
        }
    }

    private async Task<List<string>> ReadImageAsync(string source, CancellationToken cancellationToken)
    {
        byte[] pixels;
        int width, height;
        using (var image = MagickImageConverter.Read(source))
        {
            image.AutoOrient();
            if (image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }

            var max = (uint)recognizer.MaxSide;
            if (image.Width > max || image.Height > max)
            {
                image.Resize(max, max);
            }

            using var all = image.GetPixels();
            pixels = all.ToByteArray(PixelMapping.BGRA) ?? throw new InvalidDataException("没有像素");
            (width, height) = ((int)image.Width, (int)image.Height);
        }

        return Lines(await recognizer.ReadAsync(pixels, width, height, cancellationToken).ConfigureAwait(false));
    }

    private async Task<List<string>> ReadPagesAsync(string source, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        using var input = File.OpenRead(source);
        var sizes = Conversion.GetPageSizes(input, leaveOpen: true);
        for (var page = 0; page < sizes.Count; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scale = Math.Min(PdfDpi / 72.0, recognizer.MaxSide / Math.Max(sizes[page].Width, sizes[page].Height));
            input.Position = 0;
            byte[] pixels;
            int width, height;
            using (var rendered = Conversion.ToImage(input, leaveOpen: true, page: page, options: new RenderOptions(
                Width: Pixels(sizes[page].Width * scale), Height: Pixels(sizes[page].Height * scale), WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White)))
            using (var bgra = rendered.Copy(SKColorType.Bgra8888))
            {
                (width, height) = (bgra.Width, bgra.Height);
                pixels = bgra.RowBytes == width * 4 ? bgra.Bytes : throw new InvalidDataException($"第 {page + 1} 页无法渲染");
            }

            var text = Lines(await recognizer.ReadAsync(pixels, width, height, cancellationToken).ConfigureAwait(false));
            if (text.Count > 0)
            {
                if (lines.Count > 0)
                {
                    lines.Add("");
                }

                lines.AddRange(text);
            }

            progress.Report((page + 1.0) / sizes.Count);
        }

        return lines;
    }

    private static int Pixels(double size) => Math.Max(1, (int)Math.Round(size, MidpointRounding.AwayFromZero));

    private static List<string> Lines(IReadOnlyList<IReadOnlyList<string>> lines) =>
        [.. lines.Select(Join).Where(line => line.Length > 0)];

    /// <summary>The words of a line put together: a space between two words unless both sides are Chinese, Japanese or Korean.</summary>
    private static string Join(IReadOnlyList<string> words)
    {
        var line = new StringBuilder();
        foreach (var word in words.Select(w => w.Trim()).Where(w => w.Length > 0))
        {
            if (line.Length > 0 && !(IsCjk(line[^1]) && IsCjk(word[0])))
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        return line.ToString();
    }

    /// <summary>Han, kana, Hangul and the full-width punctuation that goes with them.</summary>
    private static bool IsCjk(char c) => c is
        (>= '\u2E80' and <= '\u9FFF') or (>= '\uAC00' and <= '\uD7AF') or (>= '\uF900' and <= '\uFAFF') or (>= '\uFF00' and <= '\uFFEF');
}
