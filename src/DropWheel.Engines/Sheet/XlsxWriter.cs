using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DropWheel.Engines.Sheet;

/// <summary>
/// One worksheet of text as an XLSX. A field written the plain way ("-12", "3.50") with no leading zero and at
/// most 15 digits, as many as Excel keeps, becomes a number shown with the decimals it was written with;
/// everything else is an inline string, so an ID such as 007 or a card number keeps every digit and a date stays
/// as it was typed. Hand-written like
/// <see cref="Document.DocxPackage"/>; the tests validate it with the Open XML SDK.
/// </summary>
internal static partial class XlsxWriter
{
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public const int MaxRows = XlsxReader.MaxRows;
    public const int MaxColumns = XlsxReader.MaxColumns;
    public const int MaxCellText = 32_767;

    private const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
        """;

    private const string PackageRelationships = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
        """;

    private const string WorkbookRelationships = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
        """;

    /// <summary>Decimals a number can have within its 15 digits; style n shows n of them, style 0 is General.</summary>
    private const int MaxDecimals = 15;

    /// <summary>
    /// What a style sheet needs for Excel to open the file without repairing it (one font, the two fills it
    /// reserves, one border), and a style for each count of decimals, formats 0.0 to 0.000000000000000.
    /// </summary>
    private static readonly string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
        + $"<styleSheet xmlns=\"{S}\"><numFmts count=\"{MaxDecimals}\">"
        + string.Concat(Enumerable.Range(1, MaxDecimals).Select(n => $"<numFmt numFmtId=\"{163 + n}\" formatCode=\"0.{new string('0', n)}\"/>"))
        + "</numFmts><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
        + $"<cellXfs count=\"{MaxDecimals + 1}\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
        + string.Concat(Enumerable.Range(1, MaxDecimals).Select(n => $"<xf numFmtId=\"{163 + n}\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"))
        + "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

    private static readonly XmlWriterSettings Settings = new() { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), NewLineChars = "\n" };

    /// <summary>Rows within <see cref="MaxRows"/>, fields within <see cref="MaxColumns"/> and <see cref="MaxCellText"/>: the caller checks.</summary>
    public static void Write(IReadOnlyList<string[]> rows, string sheetName, string temp, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.Open(temp, ZipArchiveMode.Create);
        Text(zip, "[Content_Types].xml", ContentTypes);
        Text(zip, "_rels/.rels", PackageRelationships);
        Text(zip, "xl/_rels/workbook.xml.rels", WorkbookRelationships);
        Text(zip, "xl/styles.xml", Styles);
        using (var entry = zip.CreateEntry("xl/workbook.xml").Open())
        using (var xml = XmlWriter.Create(entry, Settings))
        {
            xml.WriteStartElement("workbook", S);
            xml.WriteAttributeString("xmlns", "r", null, R);
            xml.WriteStartElement("sheets", S);
            xml.WriteStartElement("sheet", S);
            xml.WriteAttributeString("name", sheetName);
            xml.WriteAttributeString("sheetId", "1");
            xml.WriteAttributeString("id", R, "rId1");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        using (var entry = zip.CreateEntry("xl/worksheets/sheet1.xml").Open())
        using (var xml = XmlWriter.Create(entry, Settings))
        {
            xml.WriteStartElement("worksheet", S);
            xml.WriteStartElement("sheetData", S);
            for (var r = 0; r < rows.Count; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = rows[r];
                if (row.All(string.IsNullOrEmpty))
                {
                    continue;
                }

                var number = (r + 1).ToString(CultureInfo.InvariantCulture);
                xml.WriteStartElement("row", S);
                xml.WriteAttributeString("r", number);
                for (var c = 0; c < row.Length; c++)
                {
                    var value = Clean(row[c]);
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    xml.WriteStartElement("c", S);
                    xml.WriteAttributeString("r", Column(c) + number);
                    if (IsNumber(value))
                    {
                        // "12.50" stays 12.50 on screen, not the 12.5 a General cell shows.
                        var point = value.IndexOf('.', StringComparison.Ordinal);
                        if (point >= 0)
                        {
                            xml.WriteAttributeString("s", (value.Length - point - 1).ToString(CultureInfo.InvariantCulture));
                        }

                        xml.WriteElementString("v", S, value);
                    }
                    else
                    {
                        xml.WriteAttributeString("t", "inlineStr");
                        xml.WriteStartElement("is", S);
                        xml.WriteStartElement("t", S);
                        xml.WriteAttributeString("xml", "space", null, "preserve");
                        xml.WriteString(value.Replace("_x", "_x005F_x", StringComparison.Ordinal));
                        xml.WriteEndElement();
                        xml.WriteEndElement();
                    }

                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
        }
    }

    /// <summary>
    /// A sheet name Excel takes from <paramref name="stem"/>: without \ / ? * [ ] : or control characters, no apostrophe at either end,
    /// at most 31 characters; "Sheet1" when nothing is left or it is "History", which Excel keeps for itself.
    /// </summary>
    public static string SheetName(string stem)
    {
        var name = Clean(new string([.. stem.Where(c => c >= ' ' && c is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':'))])).Trim().Trim('\'');
        if (name.Length > 31)
        {
            name = name[..(char.IsHighSurrogate(name[30]) ? 30 : 31)];
        }

        return name.Length == 0 || name.Equals("History", StringComparison.OrdinalIgnoreCase) ? "Sheet1" : name;
    }

    /// <summary>"A", "Z", "AA" for the zero-based <paramref name="index"/>.</summary>
    private static string Column(int index)
    {
        var letters = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            letters = (char)('A' + (n - 1) % 26) + letters;
        }

        return letters;
    }

    private static bool IsNumber(string value) => PlainNumber().IsMatch(value) && value.Count(char.IsAsciiDigit) <= 15;

    /// <summary>Line breaks as LF, which Excel keeps in a cell, and what XML 1.0 has no place for taken out.</summary>
    private static string Clean(string value)
    {
        var text = value.ReplaceLineEndings("\n");
        var clean = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                clean.Append(c).Append(text[++i]);
            }
            else if (XmlConvert.IsXmlChar(c))
            {
                clean.Append(c);
            }
        }

        return clean.ToString();
    }

    private static void Text(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Settings.Encoding);
        writer.Write(content);
    }

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$")]
    private static partial Regex PlainNumber();
}
