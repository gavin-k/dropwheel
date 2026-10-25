using System.Text;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DropWheel.Engines.Pdf;

/// <summary>
/// The text layer of a PDF, in content order, through PdfPig: UTF-8 with a BOM and CRLF so Notepad on an
/// older Windows reads it right, a blank line between pages. A scanned PDF has no text layer and fails
/// instead of writing an empty file.
/// </summary>
internal static class PdfText
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public static async Task<string?> WriteAsync(
        ConversionRequest request,
        string source,
        int index,
        DateTimeOffset now,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var pages = new List<string>();
        using (var document = PdfDocument.Open(source))
        {
            var count = document.NumberOfPages;
            for (var number = 1; number <= count; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = ContentOrderTextExtractor.GetText(document.GetPage(number)).Trim('\r', '\n');
                if (!string.IsNullOrWhiteSpace(text))
                {
                    pages.Add(text);
                }

                progress.Report((double)number / count);
            }
        }

        if (pages.Count == 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(source)} 没有可提取的文字（可能是扫描件，需要 OCR）");
        }

        var all = string.Join("\n\n", pages).ReplaceLineEndings("\r\n");
        return await OutputFiles.WriteAsync(request, source, index, now,
            temp => File.WriteAllTextAsync(temp, all + "\r\n", Utf8WithBom, cancellationToken)).ConfigureAwait(false);
    }
}
