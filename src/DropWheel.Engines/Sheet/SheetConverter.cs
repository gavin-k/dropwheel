using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;

namespace DropWheel.Engines.Sheet;

/// <summary>
/// Spreadsheets in process, without Office: XLSX to CSV or TSV through <see cref="XlsxReader"/>, CSV or TSV to
/// XLSX through <see cref="XlsxWriter"/>, and CSV and TSV into each other. A workbook with one worksheet of values
/// becomes one file next to it; with more, a folder named after it holds "report-Sheet1.csv" and so on.
/// </summary>
public sealed class SheetConverter(TimeProvider? time = null) : IConverter
{
    private static readonly string[] Delimited = ["csv", "tsv"];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "sheet";

    public IEnumerable<ConversionEdge> Edges =>
    [
        .. Delimited.Select(d => new ConversionEdge("xlsx", d)),
        .. Delimited.Select(d => new ConversionEdge(d, "xlsx")),
        new("csv", "tsv"),
        new("tsv", "csv"),
    ];

    public Availability Check(ConversionRequest request) => Availability.Available;

    public QualityEstimate? Estimate(ConversionRequest request)
    {
        var from = FormatRegistry.FromPath(request.SourcePaths[0])?.Id;
        return (from, request.TargetFormatId) switch
        {
            ("xlsx", "csv" or "tsv") => new(QualityLevel.Medium,
            [
                "只留值：公式取上次算好的结果，日期写成 2026-10-25，小数位数照格式，百分比、货币符号和千位分隔符不保留",
                "每个工作表一个文件，有几个就放进同名文件夹，隐藏的和空的不导出",
                "合并单元格的值只在左上角一格，样式、批注和图表不保留",
            ]),
            ("csv" or "tsv", "xlsx") => new(QualityLevel.High, ["数字写成数字，小数位数照原样显示；以 0 开头的编号和超过 15 位的数字按文字保留", "日期、百分比和其他内容都按文字保留"]),
            _ => null,
        };
    }

    /// <summary>All or nothing, see <see cref="OutputFiles.ForEachSourceAsync"/>.</summary>
    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var to = request.TargetFormatId;
        if (to != "xlsx" && !Delimited.Contains(to))
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
        if (from == "xlsx" && Delimited.Contains(to))
        {
            var sheets = XlsxReader.Read(source, cancellationToken);
            if (sheets.Count == 0)
            {
                throw new InvalidDataException($"{name} 里没有数据");
            }

            var delimiter = to == "tsv" ? '\t' : ',';
            if (sheets is [var only])
            {
                return await OutputFiles.WriteAsync(request, source, index, now, temp =>
                {
                    Save(temp, only.Rows, delimiter, only.Width);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }

            return await OutputFiles.WriteFolderAsync(request, source, index, now, folder =>
            {
                foreach (var (sheet, file) in sheets.Zip(FileNames(FormatRegistry.StemOf(source), sheets, to)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Save(Path.Combine(folder, file), sheet.Rows, delimiter, sheet.Width);
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        if (from is null || !Delimited.Contains(from) || to == from)
        {
            throw new InvalidDataException($"{name} 转不了 {FormatRegistry.FromExtension(to)?.DisplayName ?? to}");
        }

        var rows = DelimitedText.Parse(TextFiles.Decode(await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false)), from);
        if (rows.All(row => row.All(string.IsNullOrEmpty)))
        {
            throw new InvalidDataException($"{name} 里没有数据");
        }

        if (to != "xlsx")
        {
            return await OutputFiles.WriteAsync(request, source, index, now, temp =>
            {
                Save(temp, rows, to == "tsv" ? '\t' : ',', width: 0);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        if (rows.Count > XlsxWriter.MaxRows)
        {
            throw new InvalidDataException($"{name} 有 {rows.Count} 行，超过 Excel 一个工作表的 {XlsxWriter.MaxRows} 行");
        }

        if (rows.Max(row => row.Length) is var widest && widest > XlsxWriter.MaxColumns)
        {
            throw new InvalidDataException($"{name} 有一行 {widest} 列，超过 Excel 的 {XlsxWriter.MaxColumns} 列");
        }

        if (rows.Any(row => row.Any(field => field.Length > XlsxWriter.MaxCellText)))
        {
            throw new InvalidDataException($"{name} 有一格超过 {XlsxWriter.MaxCellText} 个字符，Excel 的单元格放不下");
        }

        return await OutputFiles.WriteAsync(request, source, index, now, temp =>
        {
            XlsxWriter.Write(rows, XlsxWriter.SheetName(FormatRegistry.StemOf(source)), temp, cancellationToken);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>With a BOM like every text file this app writes, so Excel and Notepad on an older Windows read it as UTF-8.</summary>
    private static void Save(string path, IEnumerable<IReadOnlyList<string>> rows, char delimiter, int width)
    {
        using var writer = new StreamWriter(path, append: false, TextFiles.Utf8WithBom);
        DelimitedText.Write(writer, rows, delimiter, width);
    }

    /// <summary>
    /// "report-Sheet1.csv" for each sheet: what Windows does not take in a file name becomes "_", and a name that
    /// comes out the same as an earlier one, case aside, gets "-2", "-3".
    /// </summary>
    private static List<string> FileNames(string stem, IReadOnlyList<SheetTable> sheets, string extension)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var sheet in sheets)
        {
            var safe = new string([.. sheet.Name.Select(c => c < ' ' || "<>:\"/\\|?*".Contains(c) ? '_' : c)]);
            var name = $"{stem}-{safe}";
            for (var n = 2; !taken.Add(name); n++)
            {
                name = $"{stem}-{safe}-{n}";
            }

            names.Add($"{name}.{extension}");
        }

        return names;
    }
}
