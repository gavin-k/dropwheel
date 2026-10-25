using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using ImageMagick;

namespace DropWheel.Engines.Image;

/// <summary>
/// Image ↔ image via Magick.NET. M0 scope: the raster formats below, first frame only. HEIC encode
/// availability depends on the system HEVC extension and is reported, not hidden.
/// </summary>
public sealed class MagickImageConverter(TimeProvider? time = null) : IConverter
{
    private static readonly Dictionary<string, MagickFormat> Formats = new(StringComparer.Ordinal)
    {
        ["jpg"] = MagickFormat.Jpeg,
        ["png"] = MagickFormat.Png,
        ["webp"] = MagickFormat.WebP,
        ["avif"] = MagickFormat.Avif,
        ["gif"] = MagickFormat.Gif,
        ["bmp"] = MagickFormat.Bmp,
        ["tiff"] = MagickFormat.Tiff,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "magick";

    public IEnumerable<ConversionEdge> Edges =>
        from i in Formats.Keys
        from o in Formats.Keys.Append("heic")
        where i != o
        select new ConversionEdge(i, o);

    public Availability Check(ConversionRequest request)
    {
        if (request.TargetFormatId == "heic" && !SystemCodecs.HasHevcEncoder())
        {
            return Availability.Unavailable("HEIC 编码需要系统已安装「HEVC 视频扩展」") with { Cause = UnavailableCause.MissingSystemCodec };
        }

        return Availability.Available;
    }

    public QualityEstimate? Estimate(ConversionRequest request)
    {
        var from = FormatRegistry.FromPath(request.SourcePaths[0])?.Id;
        var estimate = (from, request.TargetFormatId) switch
        {
            (_, "gif") => new QualityEstimate(QualityLevel.Medium, ["GIF 仅 256 色"]),
            ("jpg" or "heic" or "webp" or "avif", "png" or "bmp" or "tiff") =>
                new(QualityLevel.High, ["无损保存，画质不会再降低", "原图若是有损压缩，已有的压缩痕迹不会消失", "文件会变大"]),
            ("png" or "gif" or "bmp" or "tiff" or "webp" or "avif", "jpg") =>
                new(QualityLevel.Medium, ["有损压缩，文字和线条边缘可能出现杂点", "透明部分变成白色"]),
            ("png" or "gif" or "tiff" or "webp" or "avif", "bmp") => new(QualityLevel.High, ["透明部分变成白色", "文件会变大"]),
            _ => null,
        };

        // Whether this GIF is animated is not read here, so every GIF gets the warning.
        return from == "gif" ? new(estimate?.Level ?? QualityLevel.High, [.. estimate?.Reasons ?? [], "只取第一帧"]) : estimate;
    }

    /// <summary>All or nothing, see <see cref="OutputFiles.ForEachSourceAsync"/>.</summary>
    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Formats.TryGetValue(request.TargetFormatId, out var format))
        {
            return new(false, [], $"不支持输出 {request.TargetFormatId}");
        }

        try
        {
            var written = await OutputFiles.ForEachSourceAsync(request, progress,
                (source, i, _) => Task.Run(() => Convert(request, source, i, format), cancellationToken), cancellationToken).ConfigureAwait(false);
            return new(true, written);
        }
        catch (MagickException ex)
        {
            return new(false, [], $"{Path.GetFileName(ex.Data["source"] as string)} 无法转换：{ex.Message}");
        }
    }

    /// <returns>The written path, or <c>null</c> when the conflict policy skipped this source.</returns>
    private string? Convert(ConversionRequest request, string source, int index, MagickFormat format)
    {
        try
        {
            using var image = new MagickImage(source);
            image.AutoOrient();
            if (format is MagickFormat.Jpeg or MagickFormat.Bmp && image.HasAlpha)
            {
                // JPEG has no alpha and most BMP readers ignore it: transparent pixels would show the colour under
                // them, usually black.
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }

            return OutputFiles.Write(request, source, index, _time.GetLocalNow(), temp => image.Write(temp, format), (int)image.Width, (int)image.Height);
        }
        catch (MagickException ex)
        {
            ex.Data["source"] = source;
            throw;
        }
    }
}

internal static class SystemCodecs
{
    /// <summary>TODO: probe WIC for the HEVC encoder (Microsoft.HEVCVideoExtension) instead of assuming.</summary>
    public static bool HasHevcEncoder() => false;
}
