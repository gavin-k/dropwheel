using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using ImageMagick;

namespace DropWheel.Engines.Image;

/// <summary>
/// Image ↔ image via Magick.NET, first frame only. Reads HEIC, ICO, SVG and PSD as well as the raster
/// formats it writes; writes ICO too. HEIC is offered as a target but always off, with the reason: the
/// Magick.NET build has no HEIC encoder, and the system's (HEVC Video Extensions, through WIC) is not wired.
/// </summary>
public sealed class MagickImageConverter(TimeProvider? time = null) : IConverter
{
    /// <summary>Why HEIC is off. It says nothing of the HEVC extension: installing it would change nothing.</summary>
    private static string HeicReason => L.T("还不能写出 HEIC，体积差不多的可以选 AVIF");

    /// <summary>Read and written alike, so the image tools can write a changed copy back in the same format.</summary>
    internal static readonly Dictionary<string, MagickFormat> Formats = new(StringComparer.Ordinal)
    {
        ["jpg"] = MagickFormat.Jpeg,
        ["png"] = MagickFormat.Png,
        ["webp"] = MagickFormat.WebP,
        ["avif"] = MagickFormat.Avif,
        ["gif"] = MagickFormat.Gif,
        ["bmp"] = MagickFormat.Bmp,
        ["tiff"] = MagickFormat.Tiff,
    };

    /// <summary>Everything it writes: <see cref="Formats"/> and icons.</summary>
    private static readonly Dictionary<string, MagickFormat> Outputs = new(Formats, StringComparer.Ordinal) { ["ico"] = MagickFormat.Ico };

    /// <summary>
    /// Everything it reads. HEIC is decoded by the libheif inside Magick.NET, not by Windows; PSD gives the
    /// merged image its file carries, ICO its largest picture, SVG is drawn at the size it states.
    /// </summary>
    private static readonly string[] Inputs = [.. Formats.Keys, "heic", "ico", "svg", "psd"];

    /// <summary>Read to convert, but not one of <see cref="Formats"/>, so the image tools cannot write it back.</summary>
    internal static bool ConvertsOnly(string formatId) => Inputs.Contains(formatId) && !Formats.ContainsKey(formatId);

    /// <summary>Whether <see cref="Read"/> takes this format.</summary>
    internal static bool Reads(string formatId) => Inputs.Contains(formatId);

    /// <summary>The largest side an icon holds, and the sizes written into one, largest first.</summary>
    private static readonly uint[] IconSizes = [256, 128, 64, 48, 32, 16];

    /// <summary>A note for every conversion from these formats, about what of the source is left out.</summary>
    private static readonly Dictionary<string, string> SourceNotes = new(StringComparer.Ordinal)
    {
        // Whether this GIF is animated is not read here, so every GIF gets the warning.
        ["gif"] = L.T("只取第一帧"),
        ["psd"] = L.T("只取合成后的图像，不保留图层"),
        ["ico"] = L.T("只取图标里最大的一张"),
        ["svg"] = L.T("按 SVG 标明的尺寸转成位图"),
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "magick";

    public IEnumerable<ConversionEdge> Edges =>
        from i in Inputs
        from o in Outputs.Keys.Append("heic")
        where i != o
        select new ConversionEdge(i, o);

    public Availability Check(ConversionRequest request)
    {
        if (request.TargetFormatId == "heic")
        {
            return Availability.Unavailable(HeicReason) with { Cause = UnavailableCause.InputOnly };
        }

        return Availability.Available;
    }

    public QualityEstimate? Estimate(ConversionRequest request)
    {
        var from = FormatRegistry.FromPath(request.SourcePaths[0])?.Id;
        var estimate = (from, request.TargetFormatId) switch
        {
            (_, "gif") => new QualityEstimate(QualityLevel.Medium, [L.T("GIF 仅 256 色")]),
            (_, "ico") => new(QualityLevel.Medium, [L.T("图标最大 256×256，大图会缩小"), L.T("不是正方形时四周补透明")]),
            ("jpg" or "heic" or "webp" or "avif", "png" or "bmp" or "tiff") =>
                new(QualityLevel.High, [L.T("无损保存，画质不会再降低"), L.T("原图若是有损压缩，已有的压缩痕迹不会消失"), L.T("文件会变大")]),
            ("png" or "gif" or "bmp" or "tiff" or "webp" or "avif" or "ico" or "svg" or "psd", "jpg") =>
                new(QualityLevel.Medium, [L.T("有损压缩，文字和线条边缘可能出现杂点"), L.T("透明部分变成白色")]),
            ("png" or "gif" or "tiff" or "webp" or "avif" or "ico" or "svg" or "psd", "bmp") => new(QualityLevel.High, [L.T("透明部分变成白色"), L.T("文件会变大")]),
            _ => null,
        };

        return from is not null && SourceNotes.TryGetValue(from, out var note)
            ? new(estimate?.Level ?? QualityLevel.High, [.. estimate?.Reasons ?? [], note])
            : estimate;
    }

    /// <summary>All or nothing, see <see cref="OutputFiles.ForEachSourceAsync"/>.</summary>
    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Outputs.TryGetValue(request.TargetFormatId, out var format))
        {
            return new(false, [], L.T("不支持输出 {0}", request.TargetFormatId));
        }

        try
        {
            var written = await OutputFiles.ForEachSourceAsync(request, progress,
                (source, i, _) => Task.Run(() => Convert(request, source, i, format, cancellationToken), cancellationToken), cancellationToken).ConfigureAwait(false);
            return ConversionResult.Written(written);
        }
        catch (Exception ex) when (ex is MagickException or InvalidDataException)
        {
            return new(false, [], L.T("{0} 无法转换：{1}", Path.GetFileName(ex.Data["source"] as string), ex.Message));
        }
    }

    /// <returns>The written path, or <c>null</c> when the conflict policy skipped this source.</returns>
    private string? Convert(ConversionRequest request, string source, int index, MagickFormat format, CancellationToken cancellationToken)
    {
        try
        {
            using var image = Read(source);
            image.AutoOrient();
            if (format is MagickFormat.Jpeg or MagickFormat.Bmp && image.HasAlpha)
            {
                // JPEG has no alpha and most BMP readers ignore it: transparent pixels would show the colour under
                // them, usually black.
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }

            if (format == MagickFormat.Ico)
            {
                FitIcon(image);
            }

            return OutputFiles.Write(request, source, index, _time.GetLocalNow(), temp => image.Write(temp, format), (int)image.Width, (int)image.Height, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is MagickException or InvalidDataException)
        {
            ex.Data["source"] = source;
            throw;
        }
    }

    /// <summary>The first frame of <paramref name="source"/>, its largest picture for an icon; an SVG that refers outside itself is refused.</summary>
    internal static MagickImage Read(string source)
    {
        switch (FormatRegistry.FromPath(source)?.Id)
        {
            case "svg":
                if (SvgGuard.FindExternal(File.ReadAllText(source)) is { } external)
                {
                    throw new InvalidDataException(L.T("SVG 引用了它以外的内容（{0}），为了安全不转换", external));
                }

                return new MagickImage(source, new MagickReadSettings { BackgroundColor = MagickColors.None });

            case "ico":
                using (var pictures = new MagickImageCollection(source))
                {
                    return (MagickImage)pictures.MaxBy(p => p.Width * p.Height)!.Clone();
                }

            default:
                return new MagickImage(source);
        }
    }

    /// <summary>
    /// Shrinks <paramref name="image"/> to what an icon holds and pads it square with transparency, then has
    /// Magick write every standard size up to that, so Windows picks a sharp one wherever it shows the icon.
    /// </summary>
    private static void FitIcon(MagickImage image)
    {
        if (image.Width > IconSizes[0] || image.Height > IconSizes[0])
        {
            image.Resize(IconSizes[0], IconSizes[0]);
        }

        var side = Math.Max(image.Width, image.Height);
        if (image.Width != image.Height)
        {
            image.Alpha(AlphaOption.Set);
            image.BackgroundColor = MagickColors.None;
            image.Extent(side, side, Gravity.Center);
        }

        var sizes = IconSizes.Where(s => s <= side).ToList();
        if (sizes.Count > 1)
        {
            image.Settings.SetDefine("icon:auto-resize", string.Join(",", sizes));
        }
    }
}
