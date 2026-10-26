using System.IO.Compression;
using System.Text;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Engines.Document;
using FluentAssertions;

namespace DropWheel.Engines.Tests;

/// <summary>PPTX to text, Markdown and HTML through <see cref="DocumentConverter"/>.</summary>
public sealed class PresentationTests : IDisposable
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private const string Ns = """xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" """;
    private const string Dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private const string RelTypes = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-slides-").FullName;
    private readonly DocumentConverter _converter = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <param name="Layout">1 a title slide, 2 a title and content, 3 a section header.</param>
    /// <param name="Rels">More relationships of the slide, such as its links.</param>
    /// <param name="Notes">The paragraphs of the speaker notes.</param>
    /// <param name="Diagram">The points of a SmartArt graphic's data, which the slide reaches as rId3.</param>
    private sealed record Slide(int Layout, string Shapes, string Attributes = "", string Rels = "", string? Notes = null, string? Diagram = null);

    private static string Shape(string? placeholder, string paragraphs, string listStyle = "") =>
        $"""<p:sp><p:nvSpPr><p:cNvPr id="2" name="形状"/><p:cNvSpPr/><p:nvPr>{(placeholder is null ? "" : $"<p:ph {placeholder}/>")}</p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle>{listStyle}</a:lstStyle>{paragraphs}</p:txBody></p:sp>""";

    private static string P(string runs, string properties = "") => $"""<a:p>{properties}{runs}<a:endParaRPr lang="zh-CN"/></a:p>""";

    private static string R(string text, string attributes = "", string children = "") => $"""<a:r><a:rPr lang="zh-CN"{attributes}>{children}</a:rPr><a:t>{text}</a:t></a:r>""";

    private static string Numbered(string start = "") => $"""<a:pPr marL="342900" indent="-342900"><a:buFont typeface="+mj-lt"/><a:buAutoNum type="arabicPeriod"{start}/></a:pPr>""";

    private static string Tree(string shapes) =>
        $"""<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{shapes}</p:spTree></p:cSld>""";

    /// <summary>
    /// A PPTX as PowerPoint saves one, cut down to what a reader looks at: a master whose body text has bullets,
    /// a title slide and a section header layout whose text has none, a title and content layout, and the slides.
    /// </summary>
    private string Deck(string name, params Slide[] slides)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string entry, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" + content);
        }

        string Rels(params (string Id, string Type, string Target)[] rels) =>
            """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">"""
            + string.Concat(rels.Select(r => $"""<Relationship Id="{r.Id}" Type="{RelTypes}{r.Type}" Target="{r.Target}"/>"""))
            + "</Relationships>";

        Entry("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/></Types>""");
        Entry("_rels/.rels", Rels(("rId1", "officeDocument", "ppt/presentation.xml")));
        Entry("ppt/presentation.xml", $"""<p:presentation {Ns}><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst><p:sldIdLst>{string.Concat(slides.Select((_, i) => $"""<p:sldId id="{256 + i}" r:id="rId{i + 2}"/>"""))}</p:sldIdLst><p:sldSz cx="12192000" cy="6858000"/></p:presentation>""");
        Entry("ppt/_rels/presentation.xml.rels", Rels([("rId1", "slideMaster", "slideMasters/slideMaster1.xml"), .. slides.Select((_, i) => ($"rId{i + 2}", "slide", $"slides/slide{i + 1}.xml"))]));

        const string Bullet = """<a:buFont typeface="Arial"/><a:buChar char="•"/>""";
        Entry("ppt/slideMasters/slideMaster1.xml", $"""<p:sldMaster {Ns}>{Tree(Shape("type=\"title\"", "") + Shape("type=\"body\" idx=\"1\"", "") + Shape("type=\"ftr\" sz=\"quarter\" idx=\"3\"", ""))}<p:txStyles><p:titleStyle><a:lvl1pPr><a:buNone/></a:lvl1pPr></p:titleStyle><p:bodyStyle><a:lvl1pPr marL="228600" indent="-228600">{Bullet}</a:lvl1pPr><a:lvl2pPr marL="685800" indent="-228600">{Bullet}</a:lvl2pPr></p:bodyStyle><p:otherStyle><a:lvl1pPr/></p:otherStyle></p:txStyles></p:sldMaster>""");
        Entry("ppt/slideMasters/_rels/slideMaster1.xml.rels", Rels([.. Enumerable.Range(1, 3).Select(i => ($"rId{i}", "slideLayout", $"../slideLayouts/slideLayout{i}.xml"))]));
        const string NoBullet = """<a:lvl1pPr marL="0" indent="0"><a:buNone/></a:lvl1pPr>""";
        string[] layouts =
        [
            Shape("type=\"ctrTitle\"", "") + Shape("type=\"subTitle\" idx=\"1\"", "", NoBullet),
            Shape("type=\"title\"", "") + Shape("idx=\"1\"", ""),
            Shape("type=\"title\"", "") + Shape("type=\"body\" idx=\"1\"", "", NoBullet),
        ];
        for (var i = 0; i < layouts.Length; i++)
        {
            Entry($"ppt/slideLayouts/slideLayout{i + 1}.xml", $"""<p:sldLayout {Ns}>{Tree(layouts[i])}</p:sldLayout>""");
            Entry($"ppt/slideLayouts/_rels/slideLayout{i + 1}.xml.rels", Rels(("rId1", "slideMaster", "../slideMasters/slideMaster1.xml")));
        }

        for (var i = 0; i < slides.Length; i++)
        {
            var slide = slides[i];
            var n = i + 1;
            Entry($"ppt/slides/slide{n}.xml", $"""<p:sld {Ns}{slide.Attributes}>{Tree(slide.Shapes)}</p:sld>""");
            List<(string, string, string)> rels = [("rId1", "slideLayout", $"../slideLayouts/slideLayout{slide.Layout}.xml")];
            if (slide.Notes is not null)
            {
                rels.Add(("rId2", "notesSlide", $"../notesSlides/notesSlide{n}.xml"));
                Entry($"ppt/notesSlides/notesSlide{n}.xml", $"""<p:notes {Ns}>{Tree(Shape("type=\"sldImg\"", "") + Shape("type=\"body\" idx=\"1\"", slide.Notes) + Shape("type=\"sldNum\" idx=\"5\"", P(R(n.ToString()))))}</p:notes>""");
            }

            if (slide.Diagram is not null)
            {
                rels.Add(("rId3", "diagramData", $"../diagrams/data{n}.xml"));
                Entry($"ppt/diagrams/data{n}.xml", $"""<dgm:dataModel xmlns:dgm="{Dgm}" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><dgm:ptLst>{slide.Diagram}</dgm:ptLst><dgm:cxnLst/></dgm:dataModel>""");
            }

            Entry($"ppt/slides/_rels/slide{n}.xml.rels", Rels([.. rels]).Replace("</Relationships>", slide.Rels + "</Relationships>"));
        }

        return path;
    }

    private static string Point(string? type, string text) =>
        $"""<dgm:pt modelId="{Guid.NewGuid():B}"{(type is null ? "" : $" type=\"{type}\"")}><dgm:prSet/><dgm:spPr/><dgm:t><a:bodyPr/><a:lstStyle/>{P(text.Length > 0 ? R(text) : "")}</dgm:t></dgm:pt>""";

    /// <summary>
    /// A title slide; a title and content slide with bullets two levels deep, numbers from 3, a text box with a link,
    /// a footer, a slide number, a group and notes; a hidden slide; a slide with only a table; a section header; and
    /// a slide with SmartArt.
    /// </summary>
    private string Report() => Deck("report.pptx",
        new Slide(1, Shape("type=\"ctrTitle\"", P(R("季度汇报"))) + Shape("type=\"subTitle\" idx=\"1\"", P(R("2026 年第三季度")))),
        new Slide(2,
            Shape("type=\"title\"", P(R("要点")))
            + Shape("idx=\"1\"", P(R("收入增长")) + P(R("华东"), "<a:pPr lvl=\"1\"/>") + P(R("成本") + R("下降", " b=\"1\""))
                + P(R("第一步"), Numbered(" startAt=\"3\"")) + P(R("第二步"), Numbered()) + P("") + P(R("插一句"), "<a:pPr marL=\"0\" indent=\"0\"><a:buNone/></a:pPr>")
                + P(R("重新数"), Numbered()) + P(R("第一行") + "<a:br><a:rPr lang=\"zh-CN\"/></a:br>" + R("第二行", " i=\"1\"")))
            + Shape(null, P(R("详见") + R("官网", "", "<a:hlinkClick r:id=\"rId9\"/>")))
            + Shape("type=\"ftr\" sz=\"quarter\" idx=\"11\"", P(R("机密")))
            + Shape("type=\"sldNum\" sz=\"quarter\" idx=\"12\"", """<a:p><a:fld id="{B6F15528-21DE-4FAA-801E-634DDDAF4B2B}" type="slidenum"><a:rPr lang="zh-CN"/><a:t>2</a:t></a:fld></a:p>""")
            + """<p:grpSp><p:nvGrpSpPr><p:cNvPr id="9" name="组合"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>""" + Shape(null, P(R("分组里的字"))) + "</p:grpSp>",
            Rels: $"""<Relationship Id="rId9" Type="{RelTypes}hyperlink" Target="https://example.com/" TargetMode="External"/>""",
            Notes: P(R("记得讲 Q4 计划"))),
        new Slide(2, Shape("type=\"title\"", P(R("隐藏页"))) + Shape("idx=\"1\"", P(R("不该出现"))), Attributes: " show=\"0\""),
        new Slide(2, """<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id="4" name="表格"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp="1"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/></p:xfrm><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/table"><a:tbl><a:tblPr firstRow="1" bandRow="1"/><a:tblGrid><a:gridCol w="1"/><a:gridCol w="1"/></a:tblGrid>"""
            + Row(Cell("地区"), Cell("收入")) + Row(Cell("华东"), Cell("120")) + Row(Cell("合计：120", " gridSpan=\"2\""), Cell("残留", " hMerge=\"1\""))
            + "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>"),
        new Slide(3, Shape("type=\"title\"", P(R("第二部分"))) + Shape("type=\"body\" idx=\"1\"", P(R("下面讲计划")))),
        new Slide(2,
            Shape("type=\"title\"", P(R("流程")))
            + $"""<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id="4" name="图示"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/></p:xfrm><a:graphic><a:graphicData uri="{Dgm}"><dgm:relIds xmlns:dgm="{Dgm}" r:dm="rId3" r:lo="rId4" r:qs="rId5" r:cs="rId6"/></a:graphicData></a:graphic></p:graphicFrame>""",
            Diagram: Point("doc", "") + Point(null, "调研") + Point("parTrans", "连线") + Point("node", "设计") + Point("pres", "") + Point("asst", "评审") + Point("sibTrans", "")));

    private static string Row(params string[] cells) => $"""<a:tr h="370840">{string.Concat(cells)}</a:tr>""";

    private static string Cell(string text, string attributes = "") => $"""<a:tc{attributes}><a:txBody><a:bodyPr/><a:lstStyle/>{P(R(text))}</a:txBody><a:tcPr/></a:tc>""";

    private Task<ConversionResult> Run(string source, string target) =>
        _converter.RunAsync(new([source], target, ""), new InlineProgress(_ => { }), CancellationToken.None);

    private async Task<string> Convert(string source, string target)
    {
        var result = await Run(source, target);
        result.Succeeded.Should().BeTrue(result.Error);
        var bytes = File.ReadAllBytes(result.OutputPaths.Single());
        if (target == "txt")
        {
            bytes.Should().StartWith(Bom, "text outputs are UTF-8 with a BOM");
            return Encoding.UTF8.GetString(bytes, Bom.Length, bytes.Length - Bom.Length);
        }

        bytes.Take(Bom.Length).Should().NotEqual(Bom, "Markdown and HTML have no BOM");
        return Encoding.UTF8.GetString(bytes);
    }

    [Fact]
    public void Presentation_out_is_medium_with_reasons()
    {
        var txt = _converter.Estimate(new(["a.pptx"], "txt", ""))!;
        txt.Level.Should().Be(QualityLevel.Medium);
        txt.Reasons[0].Should().StartWith("只留文字");
        _converter.Estimate(new(["a.pptx"], "md", ""))!.Reasons[0].Should().Contain("标题").And.Contain("表格");
        _converter.Estimate(new(["a.pptx"], "html", ""))!.Reasons.Should().Contain("隐藏的幻灯片不导出");
    }

    [Fact]
    public async Task Presentation_to_text_has_each_shown_slide_under_its_title_with_bullets_numbers_tables_smartart_and_notes()
    {
        var text = await Convert(Report(), "txt");

        text.Should().Be(string.Join("\r\n",
            "季度汇报", "2026 年第三季度", "",
            "要点", "• 收入增长", "  • 华东", "• 成本下降", "3. 第一步", "4. 第二步", "插一句", "1. 重新数", "• 第一行", "第二行", "详见官网", "分组里的字", "备注", "记得讲 Q4 计划", "",
            "幻灯片 4", "地区\t收入", "华东\t120", "合计：120\t", "",
            "第二部分", "下面讲计划", "",
            "流程", "调研", "设计", "评审") + "\r\n");
    }

    [Fact]
    public async Task Presentation_to_markdown_makes_each_slide_a_heading()
    {
        var md = await Convert(Report(), "md");

        md.Should().Be(string.Join("\n",
            "# 季度汇报", "", "2026 年第三季度", "",
            "# 要点", "", "- 收入增长", "    - 华东", "- 成本**下降**", "", "3. 第一步", "4. 第二步", "", "插一句", "", "1. 重新数", "", "- 第一行\\", "*第二行*", "",
            "详见[官网](https://example.com/)", "", "分组里的字", "", "## 备注", "", "记得讲 Q4 计划", "",
            "# 幻灯片 4", "", "| 地区 | 收入 |", "| --- | --- |", "| 华东 | 120 |", "| 合计：120 |  |", "",
            "# 第二部分", "", "下面讲计划", "",
            "# 流程", "", "调研", "", "设计", "", "评审") + "\n");
    }

    [Fact]
    public async Task Presentation_to_html_is_a_page_with_lists_from_their_numbers_tables_and_links()
    {
        var html = await Convert(Report(), "html");

        html.Should().StartWith("<!DOCTYPE html>").And.Contain("<title>report</title>");
        html.Should().Contain("<h1>要点</h1>\n<ul>\n<li>收入增长<ul>\n<li>华东</li>\n</ul>\n</li>\n<li>成本<strong>下降</strong></li>\n</ul>\n<ol start=\"3\">\n<li>第一步</li>\n<li>第二步</li>\n</ol>\n");
        html.Should().Contain("<a href=\"https://example.com/\">官网</a>").And.Contain("<h2>备注</h2>").And.Contain("<td>\n<p>合计：120</p>\n</td>");
        html.Should().NotContain("隐藏页").And.NotContain("不该出现").And.NotContain("机密").And.NotContain("残留").And.NotContain("连线");
    }

    [Fact]
    public async Task A_deck_whose_only_text_is_on_a_hidden_slide_has_no_text()
    {
        var source = Deck("pictures.pptx",
            new Slide(2, "<p:pic/>"),
            new Slide(2, Shape("type=\"title\"", P(R("草稿"))), Attributes: " show=\"false\""),
            new Slide(2, Shape("type=\"sldNum\" idx=\"12\"", P(R("3")))));

        var result = await Run(source, "md");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("pictures.pptx 里没有文字");
    }

    [Fact]
    public async Task A_password_protected_or_old_presentation_is_refused()
    {
        var source = Path.Combine(_dir, "secret.pptx");
        File.WriteAllBytes(source, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);

        (await Run(source, "txt")).Error.Should().Be("secret.pptx 有密码保护，或是改了扩展名的旧版 .ppt，暂不支持");
    }

    [Fact]
    public async Task A_file_that_is_not_a_presentation_is_refused()
    {
        var zip = Path.Combine(_dir, "notzip.pptx");
        File.WriteAllText(zip, "不是 zip");
        (await Run(zip, "txt")).Error.Should().Be("notzip.pptx 不是有效的 PPTX 文件");

        var word = Path.Combine(_dir, "word.pptx");
        using (var archive = ZipFile.Open(word, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("ppt/presentation.xml").Open());
            writer.Write("""<p:presentation xmlns:p="http://purl.oclc.org/ooxml/presentationml/main"/>""");
        }

        (await Run(word, "txt")).Error.Should().Be("word.pptx 不是 PowerPoint 演示文稿，或是 Strict Open XML 格式，暂不支持");
    }

    [Fact]
    public async Task Broken_slide_xml_is_reported_as_unreadable()
    {
        var source = Deck("broken.pptx", new Slide(2, Shape("type=\"title\"", P(R("标题")))));
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Update))
        {
            zip.GetEntry("ppt/slides/slide1.xml")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("ppt/slides/slide1.xml").Open());
            writer.Write("<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld>");
        }

        var result = await Run(source, "md");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().StartWith("broken.pptx 无法读取：");
    }
}
