using System.Text;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;

namespace DropWheel.Engines.Document;

/// <summary>
/// Word documents in process, without Office: DOCX to plain text, Markdown and HTML through <see cref="DocxReader"/>
/// and <see cref="DocxExport"/>, and plain text to DOCX through <see cref="TextDocx"/>. A password-protected DOCX
/// and an old binary .doc are refused.
/// </summary>
public sealed class DocumentConverter(TimeProvider? time = null) : IConverter
{
    private static readonly string[] WordOutputs = ["txt", "md", "html"];

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "document";

    public IEnumerable<ConversionEdge> Edges =>
        WordOutputs.Select(o => new ConversionEdge("docx", o)).Append(new ConversionEdge("txt", "docx"));

    public Availability Check(ConversionRequest request) => Availability.Available;

    public QualityEstimate? Estimate(ConversionRequest request)
    {
        var from = FormatRegistry.FromPath(request.SourcePaths[0])?.Id;
        string[] structure = ["保留标题、列表、粗体、斜体、链接和表格", "图片、字体、颜色、页眉页脚和脚注不保留，标题的自动编号也不保留", "修订按全部接受处理"];
        return (from, request.TargetFormatId) switch
        {
            ("docx", "txt") => new(QualityLevel.Medium, ["只留文字，格式、图片、页眉页脚和脚注不保留", "表格一行一行，单元格之间用 Tab 隔开", "修订按全部接受处理"]),
            ("docx", "md" or "html") => new(QualityLevel.Medium, structure),
            ("txt", "docx") => new(QualityLevel.High, ["每行一段，用 Word 默认字体"]),
            _ => null,
        };
    }

    /// <summary>All or nothing, see <see cref="OutputFiles.ForEachSourceAsync"/>.</summary>
    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var to = request.TargetFormatId;
        if (to != "docx" && !WordOutputs.Contains(to))
        {
            return new(false, [], $"不支持输出 {to}");
        }

        try
        {
            var written = await OutputFiles.ForEachSourceAsync(request, progress,
                (source, i, _) => Task.Run(() => Convert(request, source, i, cancellationToken), cancellationToken), cancellationToken).ConfigureAwait(false);
            return ConversionResult.Written(written);
        }
        catch (InvalidDataException ex)
        {
            return new(false, [], ex.Message);
        }
    }

    private async Task<string?> Convert(ConversionRequest request, string source, int index, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(source);
        var from = FormatRegistry.FromPath(source)?.Id;
        var to = request.TargetFormatId;
        var now = _time.GetLocalNow();
        if (from == "txt" && to == "docx")
        {
            var text = TextFiles.Decode(await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false));
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException($"{name} 里没有文字");
            }

            var lines = text.ReplaceLineEndings("\n").Split('\n');
            var kept = lines[^1].Length == 0 ? lines[..^1] : lines;
            return await OutputFiles.WriteAsync(request, source, index, now, temp =>
            {
                TextDocx.Write(kept, temp, cancellationToken);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        if (from != "docx" || !WordOutputs.Contains(to))
        {
            throw new InvalidDataException($"{name} 转不了 {FormatRegistry.FromExtension(to)?.DisplayName ?? to}");
        }

        var blocks = DocxReader.Read(source, cancellationToken);
        if (!DocxReader.HasText(blocks))
        {
            throw new InvalidDataException($"{name} 里没有文字");
        }

        var (output, encoding) = to switch
        {
            // With a BOM like every text file this app writes, so Notepad on an older Windows reads it right.
            "txt" => (DocxExport.Text(blocks), TextFiles.Utf8WithBom),
            "md" => (DocxExport.Markdown(blocks), (Encoding)Utf8),
            _ => (DocxExport.Html(blocks, Path.GetFileNameWithoutExtension(source)), Utf8),
        };
        return await OutputFiles.WriteAsync(request, source, index, now,
            temp => File.WriteAllTextAsync(temp, output, encoding, cancellationToken)).ConfigureAwait(false);
    }
}
