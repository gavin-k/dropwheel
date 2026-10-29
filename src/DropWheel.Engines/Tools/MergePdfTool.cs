using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Pdf;
using UglyToad.PdfPig;

namespace DropWheel.Engines.Tools;

/// <summary>
/// Two or more PDFs to one, in file name order as <see cref="CombinePdfTool"/> sorts images, named after the first
/// with "-合并". Pages are copied as they are; see <see cref="PdfCopy"/> for what does not come along.
/// </summary>
public sealed class MergePdfTool(TimeProvider? time = null) : IWheelOperation
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "tool.merge-pdf";

    public WheelMode Mode => WheelMode.Tools;

    public string Label => L.T("合并 PDF");

    public string Hint => L.T("按文件名排序");

    public bool AppliesTo(MediaKind kind) => kind == MediaKind.Pdf;

    public Availability Check(ConversionRequest request) => request.SourcePaths.Count < 2
        ? Availability.Unavailable(L.T("至少选两个 PDF")) with { Cause = UnavailableCause.SelectionShape }
        : CheckPdfs(request.SourcePaths);

    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var sources = request.SourcePaths.Order(CombinePdfTool.FileNameOrder.Instance).ToList();
        var plan = request with { SourcePaths = [sources[0]], TargetFormatId = "pdf", Naming = NamingTemplate.Default + L.T("-合并") };
        var documents = new List<PdfDocument>();
        try
        {
            return await Task.Run(() =>
            {
                foreach (var source in sources)
                {
                    documents.Add(PdfCopy.Open(source));
                }

                var total = documents.Sum(d => d.NumberOfPages);
                var pages = documents.SelectMany((d, i) => Enumerable.Range(1, d.NumberOfPages).Select(n => (d, sources[i], n)));
                var done = 0;
                var written = OutputFiles.Write(plan, sources[0], 0, _time.GetLocalNow(),
                    temp => PdfCopy.Write(temp, pages, () => progress.Report((double)++done / total), cancellationToken), cancellationToken: cancellationToken);
                return ConversionResult.Written(written is null ? [] : [written]) with { Detail = written is null ? null : L.T("{0} 个文件，共 {1} 页", sources.Count, total) };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            return new(false, [], ex.Message);
        }
        finally
        {
            documents.ForEach(d => d.Dispose());
        }
    }

    internal static Availability CheckPdfs(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (FormatRegistry.FromPath(path) is not { Id: "pdf" })
            {
                return Availability.Unavailable(L.T("{0} 不是 PDF", Path.GetFileName(path)));
            }

            if (!File.Exists(path))
            {
                return Availability.Unavailable(L.T("找不到 {0}", Path.GetFileName(path)));
            }
        }

        return Availability.Available;
    }
}
