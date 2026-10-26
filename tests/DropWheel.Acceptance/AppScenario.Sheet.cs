using System.IO.Compression;
using System.Text;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    /// <summary>XLSX → CSV through the wheel: the spreadsheet engine is registered, and a shared string, a number and a date come through.</summary>
    private static void ExcelToCsv(Checks checks, AppLog log, string dir, bool explorer)
    {
        // Its own prefix, like the archive's: Explorer may show only the stem.
        var xlsx = Path.Combine(dir, $"m1s-{DateTime.Now:HHmmss}.xlsx");
        WriteXlsx(xlsx);
        var csv = ConvertThroughWheel(checks, log, xlsx, "csv", explorer);
        if (csv is null)
        {
            return;
        }

        var bytes = File.ReadAllBytes(csv);
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        checks.Check("CSV lands next to the XLSX, UTF-8 with a BOM, with the text, the number and the date",
            SameDir(csv, xlsx) && bytes is [0xEF, 0xBB, 0xBF, ..] && text == "名称,数量\r\n苹果,3\r\n日期,2026-10-25\r\n",
            $"{csv}: {text.ReplaceLineEndings("⏎")}");
    }

    /// <summary>A small XLSX as Excel saves one: shared strings, a style sheet with the built-in date format, one worksheet.</summary>
    private static void WriteXlsx(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        Entry("[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
            """);
        Entry("_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        Entry("xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>
            """);
        Entry("xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
            """);
        Entry("xl/sharedStrings.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="4" uniqueCount="4"><si><t>名称</t></si><si><t>数量</t></si><si><t>苹果</t></si><si><t>日期</t></si></sst>
            """);
        Entry("xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="14" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs></styleSheet>
            """);
        Entry("xl/worksheets/sheet1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row><row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2"><v>3</v></c></row><row r="3"><c r="A3" t="s"><v>3</v></c><c r="B3" s="1"><v>46320</v></c></row></sheetData></worksheet>
            """);
    }
}
