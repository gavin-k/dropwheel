using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using ImageMagick;
using PDFtoImage.Exceptions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace DropWheel.Engines.Pdf;

/// <summary>
/// PDF in process: pages to PNG or JPG through PDFium (<see cref="PdfPages"/>), text and an editable DOCX
/// through PdfPig (<see cref="PdfText"/>, <see cref="PdfDocx"/>), and images to PDF through Skia's PDF writer
/// (<see cref="ImagePdf"/>), one PDF per image. A password-protected PDF is refused rather than asking for the password.
/// </summary>
public sealed class PdfConverter(TimeProvider? time = null) : IConverter
{
    private static readonly string[] PdfOutputs = ["docx", "png", "jpg", "txt"];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "pdf";

    public IEnumerable<ConversionEdge> Edges =>
        PdfOutputs.Select(o => new ConversionEdge("pdf", o))
            .Concat(ImagePdf.Inputs.Select(i => new ConversionEdge(i, "pdf")));

    public Availability Check(ConversionRequest request) => Availability.Available;

    public QualityEstimate? Estimate(ConversionRequest request)
    {
        var from = FormatRegistry.FromPath(request.SourcePaths[0])?.Id;
        var pages = $"每页一张 {PdfPages.Dpi} DPI 的图，多页时放进同名文件夹；文字不能再选中";
        return (from, request.TargetFormatId) switch
        {
            ("pdf", "png") => new(QualityLevel.High, [pages]),
            ("pdf", "jpg") => new(QualityLevel.High, [pages, "JPG 有损压缩"]),
            ("pdf", "txt") => new(QualityLevel.Medium, ["只提取文字，图片、表格和版式不保留", "扫描件没有文字层，提取不出文字"]),
            ("pdf", "docx") => new(QualityLevel.Medium,
                ["按段落重建：分栏、表格、页眉页脚不保留，字体换成默认字体", "扫描件没有文字层，提取不出文字；带文字层的扫描件只保留文字", "JPEG 2000 等少数图片格式会丢失"]),
            ("webp" or "avif", "pdf") => new(QualityLevel.High, ["PDF 不支持 WebP / AVIF 压缩，不透明的图改用 JPG 压缩"]),
            ("gif", "pdf") => new(QualityLevel.High, ["只取第一帧"]),
            ("tiff", "pdf") => new(QualityLevel.High, ["多页 TIFF 每页一页"]),
            _ => null,
        };
    }

    /// <summary>All or nothing, see <see cref="OutputFiles.ForEachSourceAsync"/>.</summary>
    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var to = request.TargetFormatId;
        if (to != "pdf" && !PdfOutputs.Contains(to))
        {
            return new(false, [], $"不支持输出 {to}");
        }

        try
        {
            var written = await OutputFiles.ForEachSourceAsync(request, progress,
                (source, i, within) => Task.Run(() => Convert(request, source, i, within, cancellationToken), cancellationToken), cancellationToken).ConfigureAwait(false);
            return ConversionResult.Written(written);
        }
        catch (InvalidDataException ex)
        {
            return new(false, [], ex.Message);
        }
    }

    private async Task<string?> Convert(ConversionRequest request, string source, int index, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var now = _time.GetLocalNow();
        var name = Path.GetFileName(source);
        try
        {
            return request.TargetFormatId switch
            {
                "pdf" => await ImagePdf.WriteAsync(request, source, index, now, progress, cancellationToken).ConfigureAwait(false),
                "txt" => await PdfText.WriteAsync(request, source, index, now, progress, cancellationToken).ConfigureAwait(false),
                "docx" => await PdfDocx.WriteAsync(request, source, index, now, progress, cancellationToken).ConfigureAwait(false),
                _ => await PdfPages.WriteAsync(request, source, index, now, progress, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (Exception ex) when (Unreadable(ex, name) is { } message)
        {
            throw new InvalidDataException(message, ex);
        }
    }

    /// <summary>What to tell the user when PDFium, PdfPig or Magick cannot read the PDF called <paramref name="name"/>; null for any other exception.</summary>
    internal static string? Unreadable(Exception ex, string name) => ex switch
    {
        PdfPasswordProtectedException or PdfUnsupportedSecuritySchemeException or PdfDocumentEncryptedException => Encrypted(name),
        PdfException or PdfDocumentFormatException or MagickException => $"{name} 无法读取：{ex.Message}",
        _ => null,
    };

    internal static string Encrypted(string name) => $"{name} 有密码保护，暂不支持加密的 PDF";
}
