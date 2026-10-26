using System.IO.Compression;
using System.Text;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    /// <summary>DOCX → Markdown through the wheel: the Word engine is registered and its heading and bold come through.</summary>
    private static void WordToMarkdown(Checks checks, AppLog log, string dir, bool explorer)
    {
        // Its own prefix, like the archive's: Explorer may show only the stem.
        var docx = Path.Combine(dir, $"m1w-{DateTime.Now:HHmmss}.docx");
        WriteDocx(docx);
        var md = ConvertThroughWheel(checks, log, docx, "md", explorer);
        if (md is null)
        {
            return;
        }

        var bytes = File.ReadAllBytes(md);
        var text = Encoding.UTF8.GetString(bytes);
        checks.Check("Markdown lands next to the DOCX, UTF-8 without a BOM, with the heading and the bold run",
            SameDir(md, docx) && bytes is not [0xEF, 0xBB, 0xBF, ..] && text == "# 标题\n\n**粗体**和正文\n",
            $"{md}: {text.ReplaceLineEndings("⏎")}");
    }

    /// <summary>The smallest DOCX Word opens: a heading by outline level, then a paragraph with a bold run.</summary>
    private static void WriteDocx(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        Entry("[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>
            """);
        Entry("_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
            """);
        Entry("word/document.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>标题</w:t></w:r></w:p><w:p><w:r><w:rPr><w:b/></w:rPr><w:t>粗体</w:t></w:r><w:r><w:t>和正文</w:t></w:r></w:p></w:body></w:document>
            """);
    }
}
