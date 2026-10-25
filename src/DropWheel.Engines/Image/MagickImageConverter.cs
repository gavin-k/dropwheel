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
        return (from, request.TargetFormatId) switch
        {
            ("jpg", "png") => new(QualityLevel.High, ["无损，文件会变大"]),
            (_, "gif") => new(QualityLevel.Medium, ["GIF 仅 256 色"]),
            _ => null,
        };
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
