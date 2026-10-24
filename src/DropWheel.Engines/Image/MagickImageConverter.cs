using DropWheel.Core.Conversion;

namespace DropWheel.Engines.Image;

/// <summary>
/// Image ↔ image via Magick.NET. M0 scope: JPG / PNG / WEBP. HEIC encode availability depends on the
/// system HEVC extension and is reported, not hidden.
/// </summary>
public sealed class MagickImageConverter : IConverter
{
    private static readonly string[] Outputs = ["jpg", "png", "webp", "avif", "gif", "bmp", "tiff", "ico", "heic", "pdf"];
    private static readonly string[] Inputs = [.. Outputs, "svg", "psd", "raw"];

    public string Id => "magick";

    public IEnumerable<ConversionEdge> Edges =>
        from i in Inputs
        from o in Outputs
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
            (_, "ico") => new(QualityLevel.Medium, ["ICO 最大 256×256，将自动缩放"]),
            (_, "gif") => new(QualityLevel.Medium, ["GIF 仅 256 色"]),
            _ => null,
        };
    }

    public Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        // TODO(M0): MagickImage.Read → Write(format) per source; report progress per file; honor cancellation.
        throw new NotImplementedException("M0: implement with Magick.NET");
    }
}

internal static class SystemCodecs
{
    /// <summary>TODO: probe WIC for the HEVC encoder (Microsoft.HEVCVideoExtension) instead of assuming.</summary>
    public static bool HasHevcEncoder() => false;
}
