using System.Globalization;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Pdf;

namespace DropWheel.Engines.Tools;

/// <summary>
/// Two or more images to one PDF, a page each, in file name order as Explorer sorts it: "scan 2" before
/// "scan 10". The PDF is named after the first image, see <see cref="ImagePdf"/> for how pages are made.
/// </summary>
public sealed class CombinePdfTool(TimeProvider? time = null) : IWheelOperation
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "tool.combine-pdf";

    public WheelMode Mode => WheelMode.Tools;

    public string Label => L.T("合成 PDF");

    public string Hint => L.T("按文件名排序");

    public string? Glyph => "\uEA90"; // PDF

    public bool AppliesTo(MediaKind kind) => kind == MediaKind.Image;

    public Availability Check(ConversionRequest request)
    {
        if (request.SourcePaths.Count < 2)
        {
            return Availability.Unavailable(L.T("至少选两张图片")) with { Cause = UnavailableCause.SelectionShape };
        }

        foreach (var path in request.SourcePaths)
        {
            if (FormatRegistry.FromPath(path) is not { } format || !ImagePdf.Inputs.Contains(format.Id))
            {
                return Availability.Unavailable(L.T("不支持 {0}", FormatRegistry.FromPath(path)?.DisplayName ?? Path.GetFileName(path)));
            }

            if (!File.Exists(path))
            {
                return Availability.Unavailable(L.T("找不到 {0}", Path.GetFileName(path)));
            }
        }

        return Availability.Available;
    }

    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var sources = request.SourcePaths.Order(FileNameOrder.Instance).ToList();
        var plan = request with { SourcePaths = [sources[0]], TargetFormatId = "pdf", Naming = NamingTemplate.Default + L.T("-合并") };
        try
        {
            var written = await Task.Run(() => ImagePdf.WriteAsync(plan, sources, 0, _time.GetLocalNow(), progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            return ConversionResult.Written(written is null ? [] : [written]) with { Detail = written is null ? null : L.T("{0} 张图片", sources.Count) };
        }
        catch (InvalidDataException ex)
        {
            return new(false, [], ex.Message);
        }
    }

    /// <summary>File names compared with their runs of digits as numbers and the rest by the current culture, ignoring case.</summary>
    internal sealed class FileNameOrder : IComparer<string>
    {
        public static readonly FileNameOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var (a, b) = (Path.GetFileName(x) ?? "", Path.GetFileName(y) ?? "");
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                var (runA, runB) = (Run(a, ref i), Run(b, ref j));
                var order = char.IsAsciiDigit(runA[0]) && char.IsAsciiDigit(runB[0])
                    ? CompareNumbers(runA, runB)
                    : CultureInfo.CurrentCulture.CompareInfo.Compare(runA, runB, CompareOptions.IgnoreCase);
                if (order != 0)
                {
                    return order;
                }
            }

            return (a.Length - i).CompareTo(b.Length - j) is var rest and not 0 ? rest : string.CompareOrdinal(a, b);
        }

        /// <summary>The digits or the other characters from <paramref name="at"/> on.</summary>
        private static string Run(string s, ref int at)
        {
            var start = at;
            var digits = char.IsAsciiDigit(s[at]);
            while (at < s.Length && char.IsAsciiDigit(s[at]) == digits)
            {
                at++;
            }

            return s[start..at];
        }

        private static int CompareNumbers(string a, string b)
        {
            var (x, y) = (a.TrimStart('0'), b.TrimStart('0'));
            return x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y) is var order and not 0 ? order : a.Length.CompareTo(b.Length);
        }
    }
}
