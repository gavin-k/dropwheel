using ImageMagick;

namespace DropWheel.Engines.Tools;

/// <summary>
/// Makes an image smaller to send. JPEG and WebP are encoded again at a lower quality, PNG and TIFF are
/// packed harder without losing anything. The copy is kept only when it is smaller, and the metadata stays.
/// </summary>
public sealed class CompressImageTool(TimeProvider? time = null) : ImageTool(time)
{
    private const int JpegQuality = 80;
    private const int WebPQuality = 75;

    public override string Id => "tool.compress-image";

    public override string Label => L.T("压缩图片");

    public override string Hint => L.T("变小才保留");

    protected override IReadOnlyCollection<string> Formats { get; } = ["jpg", "png", "webp", "tiff"];

    protected override string Suffix => L.T("-压缩");

    protected override string Unchanged => L.T("已经很小，压缩后没有变小");

    protected override string? Unsupported(Core.Conversion.FileFormat? format) => format?.Id switch
    {
        "bmp" => L.T("BMP 本身不压缩，先转成 PNG 或 JPG"),
        "gif" => L.T("GIF 已是 256 色，再压缩会损坏动画"),
        "avif" => L.T("AVIF 已经压缩得很小"),
        _ => base.Unsupported(format),
    };

    protected override string? Detail(long before, long after) => $"{Size(before)} → {Size(after)}";

    protected override Rendered? Render(string source, string formatId)
    {
        using var frames = Frames(source);
        var format = MagickFormatOf(formatId);
        foreach (var image in frames)
        {
            switch (format)
            {
                case MagickFormat.Jpeg:
                    // Never above the source's own quality, which would only add bytes.
                    image.Quality = image.Quality is > 0 and < JpegQuality ? image.Quality : JpegQuality;
                    image.Settings.Interlace = Interlace.Jpeg;
                    image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
                    break;
                case MagickFormat.Png:
                    image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                    image.Settings.SetDefine(MagickFormat.Png, "compression-filter", "5");
                    break;
                case MagickFormat.WebP:
                    image.Quality = WebPQuality;
                    image.Settings.SetDefine(MagickFormat.WebP, "method", "6");
                    break;
                case MagickFormat.Tiff:
                    image.Settings.Compression = CompressionMethod.Zip;
                    image.Settings.SetDefine(MagickFormat.Tiff, "predictor", "2");
                    break;
            }
        }

        var data = frames.ToByteArray(format);
        return data.Length < new FileInfo(source).Length ? new(data) : null;
    }

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.0} MB",
    };
}
