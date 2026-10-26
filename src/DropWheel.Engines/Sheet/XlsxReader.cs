using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DropWheel.Engines.Sheet;

/// <summary>A worksheet's values from A1, as many columns as its widest row with a value.</summary>
/// <param name="Rows">Each row's cells from column A; shorter than <paramref name="Width"/> where the rest is empty.</param>
internal sealed record SheetTable(string Name, int Width, IReadOnlyList<string[]> Rows);

/// <summary>
/// The values of an XLSX's visible worksheets, in workbook order: strings, numbers as Excel stores them rounded
/// to the 15 digits it shows, with as many decimals as a format such as 0.00 fixes, dates as ISO 8601, TRUE and
/// FALSE, error codes, and for a formula the result Excel last calculated. Other number formats (percentages,
/// currencies, thousands separators), styles, comments, charts and pictures are not read; a merged
/// cell has its value in the top-left cell only. Hidden sheets, chart sheets and sheets without a value are left
/// out. Hand-parsed through <see cref="OoxmlPackage"/>, rows streamed so a large sheet is never one XML tree.
/// </summary>
internal static partial class XlsxReader
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public const int MaxRows = 1_048_576;
    public const int MaxColumns = 16_384;

    /// <summary>Rows times columns a sheet may fill; a few bytes of XML can name a cell a billion empty cells away.</summary>
    private const long MaxCells = 100_000_000;

    /// <summary>Excel's built-in number formats that show a date, a time or both; ids 27 to 58 are the East Asian ones.</summary>
    private static readonly HashSet<int> DateFormats = [14, 15, 16, 17, 22, 27, 28, 29, 30, 31, 36, 50, 51, 52, 53, 54, 57, 58];
    private static readonly HashSet<int> TimeFormats = [18, 19, 20, 21, 22, 32, 33, 34, 35, 45, 46, 47, 55, 56];

    /// <summary>Excel's built-in number formats that fix the decimals: 0, 0.00, #,##0, #,##0.00 and the accounting ones.</summary>
    private static readonly Dictionary<int, int> FixedFormats = new() { [1] = 0, [2] = 2, [3] = 0, [4] = 2, [37] = 0, [38] = 0, [39] = 2, [40] = 2 };

    [Flags]
    private enum Shows { Number = 0, Date = 1, Time = 2 }

    /// <param name="Decimals">The decimals a number is shown with, or -1 when the format does not fix them.</param>
    private readonly record struct Format(Shows Shows, int Decimals = -1);

    public static IReadOnlyList<SheetTable> Read(string path, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(path);
        List<Cells>? sheets;
        using (var zip = OoxmlPackage.Open(path, "XLSX", ".xls"))
        {
            try
            {
                sheets = Sheets(zip, cancellationToken);
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException)
            {
                // A part that is not well-formed XML, or an entry the zip reader cannot inflate.
                throw new InvalidDataException($"{name} 无法读取：{ex.Message}", ex);
            }
        }

        if (sheets is null)
        {
            throw new InvalidDataException($"{name} 不是 Excel 工作簿，或是 Strict Open XML 格式，暂不支持");
        }

        return [.. sheets.Select(sheet => Table(sheet, name))];
    }

    /// <summary>A worksheet's cells with a value, row by row, and the rows and columns they reach.</summary>
    private sealed record Cells(string Name, List<(int Row, List<(int Column, string Text)> Cells)> Rows, int Width, int Height);

    /// <summary>The visible worksheets that have a value; null when the package's main part is not a workbook.</summary>
    private static List<Cells>? Sheets(ZipArchive zip, CancellationToken cancellationToken)
    {
        var main = OoxmlPackage.Main(zip, "xl/workbook.xml");
        var workbook = OoxmlPackage.Load(zip, main);
        if (workbook?.Root?.Name != S + "workbook")
        {
            return null;
        }

        var rels = OoxmlPackage.Relationships(zip, main);
        string? Part(string type) => rels.FirstOrDefault(r => r.Type.EndsWith(type, StringComparison.Ordinal) && !r.External).Target;
        var strings = Part("/sharedStrings") is { } shared ? SharedStrings(zip, shared) : [];
        var formats = Part("/styles") is { } styles ? Formats(OoxmlPackage.Load(zip, styles)) : [];
        var date1904 = workbook.Root.Element(S + "workbookPr")?.Attribute("date1904")?.Value is "1" or "true";

        var sheets = new List<Cells>();
        foreach (var sheet in workbook.Root.Element(S + "sheets")?.Elements(S + "sheet") ?? [])
        {
            if (sheet.Attribute("state")?.Value is "hidden" or "veryHidden"
                || rels.FirstOrDefault(r => r.Id == sheet.Attribute(R + "id")?.Value) is not { Target: { } target, External: false } rel
                || !rel.Type.EndsWith("/worksheet", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = Worksheet(zip, target, sheet.Attribute("name")?.Value ?? $"Sheet{sheets.Count + 1}", strings, formats, date1904, cancellationToken);
            if (cells?.Rows.Count > 0)
            {
                sheets.Add(cells);
            }
        }

        return sheets;
    }

    private static Cells? Worksheet(
        ZipArchive zip,
        string path,
        string sheetName,
        IReadOnlyList<string> strings,
        IReadOnlyList<Format> formats,
        bool date1904,
        CancellationToken cancellationToken)
    {
        if (OoxmlPackage.Entry(zip, path) is not { } entry)
        {
            return null;
        }

        var rows = new List<(int, List<(int, string)>)>();
        var lastRow = -1;
        var width = 0;
        var height = 0;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, OoxmlPackage.Safe);
        foreach (var row in Elements(reader, S + "row"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = int.TryParse(row.Attribute("r")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r - 1 : lastRow + 1;
            lastRow = index;
            if (index is < 0 or >= MaxRows)
            {
                continue;
            }

            var cells = new List<(int, string)>();
            var lastColumn = -1;
            foreach (var c in row.Elements(S + "c"))
            {
                var column = Column(c.Attribute("r")?.Value) ?? lastColumn + 1;
                lastColumn = column;
                var text = column < MaxColumns ? Value(c, strings, formats, date1904) : "";
                if (text.Length > 0)
                {
                    cells.Add((column, text));
                    width = Math.Max(width, column + 1);
                    height = index + 1 > height ? index + 1 : height;
                }
            }

            if (cells.Count > 0)
            {
                rows.Add((index, cells));
            }
        }

        return new Cells(sheetName, rows, width, height);
    }

    private static SheetTable Table(Cells sheet, string fileName)
    {
        if ((long)sheet.Width * sheet.Height > MaxCells)
        {
            throw new InvalidDataException($"{fileName} 的工作表「{sheet.Name}」有 {sheet.Height} 行 {sheet.Width} 列，太大了，转不了");
        }

        var table = new string[sheet.Height][];
        Array.Fill(table, []);
        foreach (var (index, cells) in sheet.Rows)
        {
            // A row given twice keeps the cells of both; a cell given twice, the later one.
            var line = table[index];
            var needed = cells.Max(c => c.Column) + 1;
            if (line.Length < needed)
            {
                Array.Resize(ref line, needed);
            }

            foreach (var (column, text) in cells)
            {
                line[column] = text;
            }

            for (var i = 0; i < line.Length; i++)
            {
                line[i] ??= "";
            }

            table[index] = line;
        }

        return new SheetTable(sheet.Name, sheet.Width, table);
    }

    private static string Value(XElement c, IReadOnlyList<string> strings, IReadOnlyList<Format> formats, bool date1904)
    {
        var v = c.Element(S + "v")?.Value;
        switch (c.Attribute("t")?.Value)
        {
            case "s":
                return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < strings.Count ? strings[i] : "";
            case "inlineStr":
                return Text(c.Element(S + "is"));
            case "b":
                return v switch { "1" => "TRUE", "0" => "FALSE", _ => v ?? "" };
            case "str" or "e" or "d":
                return Unescape(v ?? "");
        }

        if (v is null || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            return v ?? "";
        }

        var format = int.TryParse(c.Attribute("s")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s < formats.Count ? formats[s] : new(Shows.Number);
        if (format.Shows != Shows.Number && Date(number, format.Shows, date1904) is { } date)
        {
            return date;
        }

        // What Excel shows in a General cell: 15 significant digits, so 0.1 + 0.2 stored as 0.30000000000000004 is 0.3.
        var shown = number.ToString("G15", CultureInfo.InvariantCulture);
        if (shown.Contains('E', StringComparison.Ordinal))
        {
            return v;
        }

        // 12.5 under 0.00 is "12.50", but 3.7 under 0 stays "3.7": the decimals are padded, never rounded away.
        var rounded = double.Parse(shown, CultureInfo.InvariantCulture);
        return format.Decimals >= 0 && Math.Round(rounded, format.Decimals) == rounded
            ? rounded.ToString("F" + format.Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : shown;
    }

    /// <summary>"2026-10-25", "08:30:00" or both, from Excel's day count; null outside the dates Excel has.</summary>
    private static string? Date(double serial, Shows shows, bool date1904)
    {
        if (serial is < 0 or >= 2958466)
        {
            return null;
        }

        // The 1900 system counts a 29 February 1900 that never was, so day 61 is 1 March and day 1 is 1 January.
        var origin = date1904 ? new DateTime(1904, 1, 1) : serial < 61 ? new DateTime(1899, 12, 31) : new DateTime(1899, 12, 30);
        var seconds = Math.Round(serial * 86400, MidpointRounding.AwayFromZero);
        var moment = origin.AddSeconds(seconds);
        return shows switch
        {
            Shows.Date => moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Shows.Time => moment.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            _ => moment.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>What each cell style's number format shows, by the style's index.</summary>
    private static List<Format> Formats(XDocument? styles)
    {
        var root = styles?.Root;
        var custom = new Dictionary<int, Format>();
        foreach (var format in root?.Element(S + "numFmts")?.Elements(S + "numFmt") ?? [])
        {
            if (int.TryParse(format.Attribute("numFmtId")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                custom[id] = Classify(format.Attribute("formatCode")?.Value ?? "");
            }
        }

        return [.. (root?.Element(S + "cellXfs")?.Elements(S + "xf") ?? []).Select(xf =>
        {
            var id = int.TryParse(xf.Attribute("numFmtId")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            return custom.TryGetValue(id, out var format) ? format
                : new((DateFormats.Contains(id) ? Shows.Date : Shows.Number) | (TimeFormats.Contains(id) ? Shows.Time : Shows.Number), FixedFormats.GetValueOrDefault(id, -1));
        })];
    }

    /// <summary>
    /// Whether a format code shows a date or a time: its first section, without quoted text, escaped characters,
    /// padding and [colour], [condition] or [$-locale] parts, has y, d, m, h or s. An elapsed time such as
    /// [h]:mm is a duration, kept as a number. A section of only digits such as #,##0.00 fixes the decimals.
    /// </summary>
    private static Format Classify(string code)
    {
        var section = FormatNoise().Replace(code, m => m.Value.StartsWith('[') && ElapsedTime().IsMatch(m.Value) ? "\u0001" : "");
        if (section.Contains('\u0001', StringComparison.Ordinal))
        {
            return new(Shows.Number);
        }

        section = section.Split(';')[0].ToLowerInvariant();
        if (section.Contains("general", StringComparison.Ordinal))
        {
            return new(Shows.Number);
        }

        if (FixedDecimals().Match(section.Trim()) is { Success: true } digits)
        {
            return new(Shows.Number, digits.Groups[1].Length);
        }

        var date = section.IndexOfAny(['y', 'd']) >= 0;
        var time = section.IndexOfAny(['h', 's']) >= 0;
        if (!date && !time && section.Contains('m', StringComparison.Ordinal))
        {
            date = true;
        }

        return new((date ? Shows.Date : Shows.Number) | (time ? Shows.Time : Shows.Number));
    }

    /// <summary>The shared string table: each item's text, its runs joined, phonetic guides left out.</summary>
    private static List<string> SharedStrings(ZipArchive zip, string path)
    {
        if (OoxmlPackage.Entry(zip, path) is not { } entry)
        {
            return [];
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, OoxmlPackage.Safe);
        return [.. Elements(reader, S + "si").Select(Text)];
    }

    private static string Text(XElement? item) =>
        item is null ? "" : Unescape(string.Concat(item.Descendants(S + "t").Where(t => t.Parent?.Name != S + "rPh").Select(t => t.Value)));

    /// <summary>Each <paramref name="name"/> element in document order, read one at a time.</summary>
    private static IEnumerable<XElement> Elements(XmlReader reader, XName name)
    {
        reader.MoveToContent();
        while (!reader.EOF)
        {
            // ReadFrom leaves the reader on the next node, which may be the next element wanted.
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == name.LocalName && reader.NamespaceURI == name.NamespaceName)
            {
                yield return (XElement)XNode.ReadFrom(reader);
            }
            else
            {
                reader.Read();
            }
        }
    }

    /// <summary>The zero-based column of a reference such as "AB12"; null when it has none.</summary>
    private static int? Column(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var column = 0;
        var letters = 0;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                column = column * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
                if (++letters > 3)
                {
                    return null;
                }
            }
            else
            {
                break;
            }
        }

        return letters == 0 ? null : column - 1;
    }

    /// <summary>OOXML writes a character XML cannot hold, such as a carriage return, as _x000D_, and a literal "_x" as _x005F_x.</summary>
    private static string Unescape(string text) =>
        text.Contains("_x", StringComparison.Ordinal)
            ? Escaped().Replace(text, m => ((char)int.Parse(m.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString())
            : text;

    [GeneratedRegex("_x([0-9A-Fa-f]{4})_")]
    private static partial Regex Escaped();

    [GeneratedRegex("\"[^\"]*\"|\\\\.|[_*].|\\[[^\\]]*\\]")]
    private static partial Regex FormatNoise();

    [GeneratedRegex(@"^\[(h+|m+|s+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex ElapsedTime();

    /// <summary>At most 15 decimals, as many as <see cref="Math.Round(double, int)"/> takes.</summary>
    [GeneratedRegex(@"^[#,]*0[0,]*(?:\.(0{1,15}))?$")]
    private static partial Regex FixedDecimals();
}
