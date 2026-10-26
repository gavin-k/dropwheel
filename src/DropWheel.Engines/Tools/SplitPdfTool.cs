using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Pdf;

namespace DropWheel.Engines.Tools;

/// <summary>
/// Each page of a PDF to a PDF of its own, in a folder named after it, as "report-01.pdf" and so on, numbered the
/// way <see cref="PdfPages"/> numbers page images. A one-page PDF is left as it is. See <see cref="PdfCopy"/>.
/// </summary>
public sealed class SplitPdfTool(TimeProvider? time = null) : IWheelOperation
{
    private static string OnePage => L.T("只有一页，不用拆分");

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "tool.split-pdf";

    public WheelMode Mode => WheelMode.Tools;

    public string Label => L.T("拆分 PDF");

    public string Hint => L.T("每页一个");

    public bool AppliesTo(MediaKind kind) => kind == MediaKind.Pdf;

    public Availability Check(ConversionRequest request) => MergePdfTool.CheckPdfs(request.SourcePaths);

    /// <summary>All or nothing, see <see cref="OutputFiles.ForEachSourceAsync"/>.</summary>
    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var now = _time.GetLocalNow();
        var plan = request with { TargetFormatId = "pdf", Naming = NamingTemplate.Default };
        int unchanged = 0, pages = 0;
        try
        {
            var written = await OutputFiles.ForEachSourceAsync(request, progress, (source, i, within) => Task.Run(async () =>
            {
                using var document = PdfCopy.Open(source);
                var count = document.NumberOfPages;
                if (count < 2)
                {
                    unchanged++;
                    return null;
                }

                var stem = FormatRegistry.StemOf(source);
                var folder = await OutputFiles.WriteFolderAsync(plan, source, i, now, temp =>
                {
                    for (var number = 1; number <= count; number++)
                    {
                        PdfCopy.Write(Path.Combine(temp, PdfPages.FileName(stem, number, count, "pdf")), [(document, source, number)], () => { }, cancellationToken);
                        within.Report((double)number / count);
                    }

                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                pages += folder is null ? 0 : count;
                return folder;
            }, cancellationToken), cancellationToken).ConfigureAwait(false);

            if (unchanged == request.SourcePaths.Count)
            {
                return new(true, []) { Skipped = true, Detail = OnePage };
            }

            var notes = new[] { written.Count > 0 ? L.T("共 {0} 页", pages) : null, unchanged > 0 ? L.T("另有 {0} 个{1}", unchanged, OnePage) : null };
            return ConversionResult.Written(written) with { Detail = string.Join(L.T("；"), notes.OfType<string>()) is { Length: > 0 } d ? d : null };
        }
        catch (InvalidDataException ex)
        {
            return new(false, [], ex.Message);
        }
    }
}
