using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Engines.Sheet;
using FluentAssertions;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace DropWheel.Engines.Tests;

public sealed class SheetConverterTests : IDisposable
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-sheet-").FullName;
    private readonly SheetConverter _converter = new();
    private readonly List<X.SharedStringItem> _shared = [];

    static SheetConverterTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// An XLSX made with the Open XML SDK, as Excel would save it, with the strings of <see cref="Text"/> and <see cref="Rich"/> shared.
    /// Styles: 0 General, 1 a built-in date, 2 "yyyy/mm/dd hh:mm", 3 a percentage, 4 "[h]:mm", 5 "0.00E+00",
    /// 6 the built-in 0.00, 7 the built-in 0, 8 a currency with two decimals.
    /// </summary>
    private string Workbook(string name, params (string Name, bool Hidden, X.Row[] Rows)[] sheets)
    {
        var path = Path.Combine(_dir, name);
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new X.Workbook();
        workbook.AddNewPart<SharedStringTablePart>().SharedStringTable = new X.SharedStringTable(_shared);
        workbook.AddNewPart<WorkbookStylesPart>().Stylesheet = new X.Stylesheet(
            new X.NumberingFormats(
                new X.NumberingFormat { NumberFormatId = 164, FormatCode = "yyyy/mm/dd hh:mm" },
                new X.NumberingFormat { NumberFormatId = 165, FormatCode = "[h]:mm" },
                new X.NumberingFormat { NumberFormatId = 166, FormatCode = "0.00E+00" },
                new X.NumberingFormat { NumberFormatId = 167, FormatCode = "\"¥\"#,##0.00_);[Red](\"¥\"#,##0.00)" }) { Count = 4 },
            new X.Fonts(new X.Font()) { Count = 1 },
            new X.Fills(new X.Fill(new X.PatternFill { PatternType = X.PatternValues.None }), new X.Fill(new X.PatternFill { PatternType = X.PatternValues.Gray125 })) { Count = 2 },
            new X.Borders(new X.Border()) { Count = 1 },
            new X.CellFormats(Xf(0), Xf(14), Xf(164), Xf(10), Xf(165), Xf(166), Xf(2), Xf(1), Xf(167)) { Count = 9 });
        var list = workbook.Workbook.AppendChild(new X.Sheets());
        uint id = 1;
        foreach (var (sheetName, hidden, rows) in sheets)
        {
            var part = workbook.AddNewPart<WorksheetPart>();
            part.Worksheet = new X.Worksheet(new X.SheetData(rows));
            var sheet = new X.Sheet { Name = sheetName, SheetId = id++, Id = workbook.GetIdOfPart(part) };
            if (hidden)
            {
                sheet.State = X.SheetStateValues.Hidden;
            }

            list.Append(sheet);
        }

        _shared.Clear();
        return path;

        static X.CellFormat Xf(uint format) => new() { NumberFormatId = format, ApplyNumberFormat = true };
    }

    private static X.Row Row(uint index, params X.Cell[] cells) => new(cells) { RowIndex = index };

    private X.Cell Text(string reference, string text) => Shared(reference, new X.SharedStringItem(new X.Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    /// <summary>A shared string as Excel saves one with formatting in the cell: runs, then a phonetic guide that is not text.</summary>
    private X.Cell Rich(string reference, string plain, string bold, string phonetic) => Shared(reference, new X.SharedStringItem(
        new X.Run(new X.Text(plain)),
        new X.Run(new X.RunProperties(new X.Bold()), new X.Text(bold)),
        new X.PhoneticRun(new X.Text(phonetic)) { BaseTextStartIndex = 0, EndingBaseIndex = 1 }));

    private X.Cell Shared(string reference, X.SharedStringItem item)
    {
        _shared.Add(item);
        return new() { CellReference = reference, DataType = X.CellValues.SharedString, CellValue = new X.CellValue((_shared.Count - 1).ToString()) };
    }

    private static X.Cell Number(string reference, string value, uint style = 0) =>
        new() { CellReference = reference, CellValue = new X.CellValue(value), StyleIndex = style };

    private Task<ConversionResult> Run(string source, string target, string? output = null) =>
        _converter.RunAsync(new([source], target, output ?? ""), new InlineProgress(_ => { }), CancellationToken.None);

    private static string Decoded(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes.Should().StartWith(Bom, "text outputs are UTF-8 with a BOM");
        return Encoding.UTF8.GetString(bytes, Bom.Length, bytes.Length - Bom.Length);
    }

    private async Task<string> ConvertText(string source, string target, string? output = null)
    {
        var result = await Run(source, target, output);
        result.Succeeded.Should().BeTrue(result.Error);
        return Decoded(result.OutputPaths.Single());
    }

    private string Csv(string name, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, (encoding ?? new UTF8Encoding(false)).GetBytes(text));
        return path;
    }

    /// <summary>A package with only the parts a reader needs, xl/workbook.xml as given.</summary>
    private string Package(string name, string workbook)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string entry, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(content);
        }

        Entry("[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/></Types>
            """);
        Entry("_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        Entry("xl/workbook.xml", workbook);
        return path;
    }

    /// <summary>The sheet name and the cells of an XLSX this converter wrote, after checking it against the Office 2019 schema.</summary>
    private static (string Name, Dictionary<string, (bool Text, string Value)> Cells) Cells(string xlsx)
    {
        using var document = SpreadsheetDocument.Open(xlsx, isEditable: false);
        new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document)
            .Select(e => $"{e.Path?.XPath}: {e.Description}").Should().BeEmpty();
        var workbook = document.WorkbookPart!;
        var sheet = workbook.Workbook!.Sheets!.Elements<X.Sheet>().Single();
        var data = ((WorksheetPart)workbook.GetPartById(sheet.Id!)).Worksheet!.GetFirstChild<X.SheetData>()!;
        var cells = data.Descendants<X.Cell>().ToDictionary(
            c => c.CellReference!.Value!,
            c => c.DataType?.Value == X.CellValues.InlineString ? (true, c.InlineString!.InnerText) : (false, c.CellValue!.Text));
        return (sheet.Name!, cells);
    }

    [Fact]
    public void Edges_are_xlsx_to_csv_and_tsv_and_back_and_csv_and_tsv_into_each_other()
    {
        _converter.Edges.Should().BeEquivalentTo(new ConversionEdge[]
        {
            new("xlsx", "csv"), new("xlsx", "tsv"), new("csv", "xlsx"), new("tsv", "xlsx"), new("csv", "tsv"), new("tsv", "csv"),
        });
    }

    [Fact]
    public void Workbook_out_is_medium_and_text_in_is_high_with_reasons()
    {
        var csv = _converter.Estimate(new(["a.xlsx"], "csv", ""))!;
        csv.Level.Should().Be(QualityLevel.Medium);
        csv.Reasons[0].Should().Contain("公式").And.Contain("日期");
        var xlsx = _converter.Estimate(new(["a.csv"], "xlsx", ""))!;
        xlsx.Level.Should().Be(QualityLevel.High);
        xlsx.Reasons[0].Should().Contain("以 0 开头");
        _converter.Estimate(new(["a.csv"], "tsv", "")).Should().BeNull();
    }

    [Fact]
    public async Task A_workbook_with_one_sheet_of_values_becomes_one_csv_with_rich_text_dates_decimals_and_cached_results()
    {
        var source = Workbook("report.xlsx",
            ("数据", false,
            [
                Row(1, Text("A1", "项目"), Rich("B1", "值", "（元）", "zhi")),
                Row(2, Text("A2", "小数"), Number("B2", "0.30000000000000004")),
                Row(3, Text("A3", "日期"), Number("B3", "46320", style: 1)),
                Row(4, Text("A4", "时间"), Number("B4", "46320.604166666664", style: 2)),
                Row(5, Text("A5", "百分比"), Number("B5", "0.25", style: 3)),
                Row(6, Text("A6", "时长"), Number("B6", "1.5", style: 4)),
                Row(7, Text("A7", "科学计数"), Number("B7", "12345678", style: 5)),
                Row(8, Text("A8", "布尔"), new X.Cell { CellReference = "B8", DataType = X.CellValues.Boolean, CellValue = new X.CellValue("1") }),
                Row(9, Text("A9", "公式"), new X.Cell(new X.CellFormula("B2*2"), new X.CellValue("0.6")) { CellReference = "B9" }),
                Row(10, Text("A10", "引号"), Text("B10", "a, \"b\"")),
                Row(11, Text("A11", "内联"), new X.Cell(new X.InlineString(new X.Text("第一行\n第二行"))) { CellReference = "B11", DataType = X.CellValues.InlineString }),
                Row(13, Text("B13", "跳过一行和一格")),
                Row(14, Text("A14", "两位小数"), Number("B14", "12.5", style: 6)),
                Row(15, Text("A15", "整数格式"), Number("B15", "3.7", style: 7)),
                Row(16, Text("A16", "货币"), Number("B16", "1234.5", style: 8)),
            ]),
            ("隐藏", true, [Row(1, Text("A1", "不导出"))]),
            ("空", false, []));

        var result = await Run(source, "csv");

        result.Succeeded.Should().BeTrue(result.Error);
        result.OutputPaths.Should().Equal(Path.Combine(_dir, "report.csv"));
        Decoded(result.OutputPaths[0]).Should().Be(
            "项目,值（元）\r\n小数,0.3\r\n日期,2026-10-25\r\n时间,2026-10-25 14:30:00\r\n百分比,0.25\r\n时长,1.5\r\n科学计数,12345678\r\n" +
            "布尔,TRUE\r\n公式,0.6\r\n引号,\"a, \"\"b\"\"\"\r\n内联,\"第一行\n第二行\"\r\n,\r\n,跳过一行和一格\r\n" +
            "两位小数,12.50\r\n整数格式,3.7\r\n货币,1234.50\r\n");
    }

    [Fact]
    public async Task Several_sheets_go_into_a_folder_named_after_the_workbook()
    {
        var source = Workbook("book.xlsx",
            ("Sheet1", false, [Row(1, Number("A1", "1"), Number("C1", "3"))]),
            ("a<b", false, [Row(1, Text("A1", "x\ty"))]),
            ("A>B", false, [Row(2, Text("B2", "y"))]));

        var result = await Run(source, "tsv");

        result.Succeeded.Should().BeTrue(result.Error);
        var folder = Path.Combine(_dir, "book");
        result.OutputPaths.Should().Equal(folder);
        Directory.GetFiles(folder).Select(Path.GetFileName).Should().BeEquivalentTo(["book-Sheet1.tsv", "book-a_b.tsv", "book-A_B-2.tsv"]);
        Decoded(Path.Combine(folder, "book-Sheet1.tsv")).Should().Be("1\t\t3\r\n");
        Decoded(Path.Combine(folder, "book-a_b.tsv")).Should().Be("\"x\ty\"\r\n");
        Decoded(Path.Combine(folder, "book-A_B-2.tsv")).Should().Be("\t\r\n\ty\r\n");
    }

    [Fact]
    public async Task A_workbook_with_nothing_shown_has_no_data()
    {
        var source = Workbook("empty.xlsx", ("Sheet1", false, []), ("隐藏", true, [Row(1, Text("A1", "x"))]));

        (await Run(source, "csv")).Error.Should().Be("empty.xlsx 里没有数据");
    }

    [Fact]
    public async Task A_password_protected_workbook_is_refused()
    {
        var source = Path.Combine(_dir, "secret.xlsx");
        File.WriteAllBytes(source, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);

        (await Run(source, "csv")).Error.Should().Be("secret.xlsx 有密码保护，或是改了扩展名的旧版 .xls，暂不支持");
    }

    [Fact]
    public async Task A_file_that_is_not_a_zip_is_refused()
    {
        var source = Path.Combine(_dir, "fake.xlsx");
        File.WriteAllText(source, "hello");

        (await Run(source, "csv")).Error.Should().Be("fake.xlsx 不是有效的 XLSX 文件");
    }

    [Fact]
    public async Task A_strict_or_foreign_package_is_refused()
    {
        var source = Package("strict.xlsx", "<workbook xmlns=\"http://purl.oclc.org/ooxml/spreadsheetml/main\"/>");

        (await Run(source, "csv")).Error.Should().Be("strict.xlsx 不是 Excel 工作簿，或是 Strict Open XML 格式，暂不支持");
    }

    [Fact]
    public async Task Broken_xml_is_reported_as_unreadable()
    {
        var source = Package("broken.xlsx", $"<workbook xmlns=\"{S}\"><sheets>");

        (await Run(source, "tsv")).Error.Should().StartWith("broken.xlsx 无法读取：");
    }

    [Fact]
    public async Task Literal_ooxml_escape_sequences_survive_a_csv_xlsx_round_trip()
    {
        const string content = "_x0041_,_x005F_,_x000D_,_x005F_x0041_,ordinary_xtext\r\n";
        var source = Csv("literal.csv", content);
        var xlsx = await Run(source, "xlsx");
        xlsx.Succeeded.Should().BeTrue(xlsx.Error);
        var csv = await Run(xlsx.OutputPaths.Single(), "csv");
        csv.Succeeded.Should().BeTrue(csv.Error);
        File.ReadAllText(csv.OutputPaths.Single()).Should().Be(content);
    }

    [Fact]
    public async Task Csv_to_xlsx_keeps_ids_long_numbers_and_dates_as_text_and_validates()
    {
        var source = Csv("季度[1].csv", "编号,数量,金额,卡号,日期,比例,备注\r\n007,12,-3.5,1234567890123456,2026-10-25,50%,\"a,b\"\r\n,,,,,,\"第一行\n第二行\"\r\n");

        var result = await Run(source, "xlsx");

        result.Succeeded.Should().BeTrue(result.Error);
        var (name, cells) = Cells(result.OutputPaths.Single());
        name.Should().Be("季度1");
        cells["A2"].Should().Be((true, "007"));
        cells["B2"].Should().Be((false, "12"));
        cells["C2"].Should().Be((false, "-3.5"));
        cells["D2"].Should().Be((true, "1234567890123456"));
        cells["E2"].Should().Be((true, "2026-10-25"));
        cells["F2"].Should().Be((true, "50%"));
        cells["G2"].Should().Be((true, "a,b"));
        cells.Keys.Where(k => k.EndsWith('3')).Should().Equal("G3");
        cells["G3"].Should().Be((true, "第一行\n第二行"));
    }

    [Fact]
    public async Task Csv_to_xlsx_and_back_is_the_same_text_decimals_included()
    {
        const string text = "名称,数量,备注\r\n苹果,3,\"红的, \"\"甜的\"\"\"\r\n,,\"两\n行\"\r\n香蕉,0.50,\r\n西瓜,-12.000,\r\n";
        var source = Csv("fruit.csv", text);
        var xlsx = (await Run(source, "xlsx")).OutputPaths.Single();
        var back = Directory.CreateDirectory(Path.Combine(_dir, "back")).FullName;

        (await ConvertText(xlsx, "csv", back)).Should().Be(text);
    }

    [Fact]
    public async Task A_csv_with_semicolons_or_a_sep_line_is_split_on_them()
    {
        (await ConvertText(Csv("eu.csv", "a;b;c\r\n1,5;2,5;3\r\n"), "tsv")).Should().Be("a\tb\tc\r\n1,5\t2,5\t3\r\n");
        (await ConvertText(Csv("sep.csv", "sep=|\r\na|b\r\nc|\"d|e\"\r\n"), "tsv")).Should().Be("a\tb\r\nc\td|e\r\n");
    }

    [Fact]
    public async Task A_gbk_tsv_becomes_a_utf8_csv()
    {
        var source = Csv("gbk.tsv", "名称\t说明\r\n苹果\t红的, 甜的\r\n", Encoding.GetEncoding(936));

        (await ConvertText(source, "csv")).Should().Be("名称,说明\r\n苹果,\"红的, 甜的\"\r\n");
    }

    [Fact]
    public async Task Text_with_no_fields_has_no_data()
    {
        (await Run(Csv("empty.csv", "\r\n,,\r\n"), "xlsx")).Error.Should().Be("empty.csv 里没有数据");
    }

    [Fact]
    public async Task Text_beyond_what_a_sheet_holds_is_refused()
    {
        (await Run(Csv("wide.csv", "x" + new string(',', 16_384)), "xlsx")).Error.Should().Be("wide.csv 有一行 16385 列，超过 Excel 的 16384 列");
        (await Run(Csv("long.csv", new string('x', 32_768)), "xlsx")).Error.Should().Be("long.csv 有一格超过 32767 个字符，Excel 的单元格放不下");
    }

    [Fact]
    public async Task The_sheet_name_is_cut_to_what_excel_takes()
    {
        var stem = string.Concat(Enumerable.Repeat("一二三四五六七八九十", 4));
        Cells((await Run(Csv(stem + ".csv", "x\r\n"), "xlsx")).OutputPaths.Single()).Name.Should().Be(stem[..31]);
        Cells((await Run(Csv("History.csv", "x\r\n"), "xlsx")).OutputPaths.Single()).Name.Should().Be("Sheet1");
    }

    [Fact]
    public async Task Other_targets_are_refused()
    {
        (await Run(Csv("a.csv", "x\r\n"), "pdf")).Error.Should().Be("不支持输出 pdf");
    }
}
