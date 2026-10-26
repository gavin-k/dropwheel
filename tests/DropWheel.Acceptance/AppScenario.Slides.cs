using System.IO.Compression;
using System.Text;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    /// <summary>PPTX → Markdown through the wheel: the slide's title, a bullet it takes from the master, and the speaker notes come through.</summary>
    private static void PowerPointToMarkdown(Checks checks, AppLog log, string dir, bool explorer)
    {
        // Its own prefix, like the archive's: Explorer may show only the stem.
        var pptx = Path.Combine(dir, $"m1p-{DateTime.Now:HHmmss}.pptx");
        WritePptx(pptx);
        var md = ConvertThroughWheel(checks, log, pptx, "md", explorer);
        if (md is null)
        {
            return;
        }

        var bytes = File.ReadAllBytes(md);
        var text = Encoding.UTF8.GetString(bytes);
        checks.Check("Markdown lands next to the PPTX, UTF-8 without a BOM, with the title, the master's bullet and the notes",
            SameDir(md, pptx) && bytes is not [0xEF, 0xBB, 0xBF, ..] && text == "# 汇报\n\n- 要点\n\n## 备注\n\n记得讲\n",
            $"{md}: {text.ReplaceLineEndings("⏎")}");
    }

    /// <summary>
    /// What the reader follows of a deck, with the relative targets PowerPoint writes: one slide on a layout on a
    /// master whose body text has bullets, a title and a body placeholder, and speaker notes. No theme or notes
    /// master, so not a deck PowerPoint itself would open.
    /// </summary>
    private static void WritePptx(string path)
    {
        const string Ns = """xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" """;
        const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        static string Shape(string placeholder, string text) =>
            $"""<p:sp><p:nvSpPr><p:cNvPr id="2" name="形状"/><p:cNvSpPr/><p:nvPr><p:ph {placeholder}/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>{(text == "" ? "" : $"<a:p><a:r><a:rPr lang=\"zh-CN\"/><a:t>{text}</a:t></a:r></a:p>")}</p:txBody></p:sp>""";
        static string Tree(string shapes) =>
            $"""<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{shapes}</p:spTree></p:cSld>""";
        static string Rels(params string[] rels) =>
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{string.Concat(rels)}</Relationships>""";

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        Entry("[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/><Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/><Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/><Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/><Override PartName="/ppt/notesSlides/notesSlide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml"/></Types>
            """);
        Entry("_rels/.rels", Rels($"""<Relationship Id="rId1" Type="{Rel}officeDocument" Target="ppt/presentation.xml"/>"""));
        Entry("ppt/presentation.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:presentation {Ns}><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst><p:sldIdLst><p:sldId id="256" r:id="rId2"/></p:sldIdLst><p:sldSz cx="12192000" cy="6858000"/><p:notesSz cx="6858000" cy="9144000"/></p:presentation>""");
        Entry("ppt/_rels/presentation.xml.rels", Rels(
            $"""<Relationship Id="rId1" Type="{Rel}slideMaster" Target="slideMasters/slideMaster1.xml"/>""",
            $"""<Relationship Id="rId2" Type="{Rel}slide" Target="slides/slide1.xml"/>"""));
        Entry("ppt/slideMasters/slideMaster1.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldMaster {Ns}>{Tree(Shape("type=\"title\"", "") + Shape("type=\"body\" idx=\"1\"", ""))}<p:txStyles><p:titleStyle><a:lvl1pPr><a:buNone/></a:lvl1pPr></p:titleStyle><p:bodyStyle><a:lvl1pPr marL="228600" indent="-228600"><a:buFont typeface="Arial"/><a:buChar char="•"/></a:lvl1pPr></p:bodyStyle><p:otherStyle/></p:txStyles></p:sldMaster>""");
        Entry("ppt/slideMasters/_rels/slideMaster1.xml.rels", Rels($"""<Relationship Id="rId1" Type="{Rel}slideLayout" Target="../slideLayouts/slideLayout1.xml"/>"""));
        Entry("ppt/slideLayouts/slideLayout1.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldLayout {Ns} type="obj">{Tree(Shape("type=\"title\"", "") + Shape("idx=\"1\"", ""))}</p:sldLayout>""");
        Entry("ppt/slideLayouts/_rels/slideLayout1.xml.rels", Rels($"""<Relationship Id="rId1" Type="{Rel}slideMaster" Target="../slideMasters/slideMaster1.xml"/>"""));
        Entry("ppt/slides/slide1.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sld {Ns}>{Tree(Shape("type=\"title\"", "汇报") + Shape("idx=\"1\"", "要点"))}</p:sld>""");
        Entry("ppt/slides/_rels/slide1.xml.rels", Rels(
            $"""<Relationship Id="rId1" Type="{Rel}slideLayout" Target="../slideLayouts/slideLayout1.xml"/>""",
            $"""<Relationship Id="rId2" Type="{Rel}notesSlide" Target="../notesSlides/notesSlide1.xml"/>"""));
        Entry("ppt/notesSlides/notesSlide1.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:notes {Ns}>{Tree(Shape("type=\"sldImg\"", "") + Shape("type=\"body\" idx=\"1\"", "记得讲"))}</p:notes>""");
    }
}
