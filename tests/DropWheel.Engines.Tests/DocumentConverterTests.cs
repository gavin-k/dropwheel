using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Engines.Document;
using FluentAssertions;
using Wp = DocumentFormat.OpenXml.Wordprocessing;

namespace DropWheel.Engines.Tests;

public sealed class DocumentConverterTests : IDisposable
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-document-").FullName;
    private readonly DocumentConverter _converter = new();

    static DocumentConverterTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A DOCX made with the Open XML SDK, as Word would save it, with the styles and lists of <see cref="Report"/>.</summary>
    private string Word(string name, Func<MainDocumentPart, IEnumerable<OpenXmlElement>> body)
    {
        var path = Path.Combine(_dir, name);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.AddNewPart<StyleDefinitionsPart>().Styles = new Wp.Styles(
            new Wp.Style(new Wp.StyleName { Val = "Title" }) { Type = Wp.StyleValues.Paragraph, StyleId = "Title" },
            new Wp.Style(new Wp.StyleName { Val = "heading 1" }, new Wp.StyleRunProperties(new Wp.Bold())) { Type = Wp.StyleValues.Paragraph, StyleId = "Heading1" },
            new Wp.Style(new Wp.StyleName { Val = "My Heading" }, new Wp.BasedOn { Val = "Heading1" }) { Type = Wp.StyleValues.Paragraph, StyleId = "MyHeading", CustomStyle = true },
            new Wp.Style(new Wp.StyleName { Val = "Strong" }, new Wp.StyleRunProperties(new Wp.Bold())) { Type = Wp.StyleValues.Character, StyleId = "Strong" });
        main.AddNewPart<NumberingDefinitionsPart>().Numbering = new Wp.Numbering(
            new Wp.AbstractNum(Level(0, Wp.NumberFormatValues.Bullet, 1), Level(1, Wp.NumberFormatValues.Bullet, 1)) { AbstractNumberId = 0 },
            new Wp.AbstractNum(Level(0, Wp.NumberFormatValues.Decimal, 1)) { AbstractNumberId = 1 },
            new Wp.AbstractNum(Level(0, Wp.NumberFormatValues.Decimal, 3)) { AbstractNumberId = 2 },
            new Wp.NumberingInstance(new Wp.AbstractNumId { Val = 0 }) { NumberID = 1 },
            new Wp.NumberingInstance(new Wp.AbstractNumId { Val = 1 }) { NumberID = 2 },
            new Wp.NumberingInstance(new Wp.AbstractNumId { Val = 2 }) { NumberID = 3 });
        main.Document = new Wp.Document(new Wp.Body(body(main)));
        return path;

        static Wp.Level Level(int index, Wp.NumberFormatValues format, int start) =>
            new(new Wp.StartNumberingValue { Val = start }, new Wp.NumberingFormat { Val = format }, new Wp.LevelText { Val = format == Wp.NumberFormatValues.Bullet ? "•" : "%1." }) { LevelIndex = index };
    }

    private static Wp.Paragraph P(string? style, params OpenXmlElement[] content)
    {
        var p = new Wp.Paragraph();
        if (style is not null)
        {
            p.Append(new Wp.ParagraphProperties(new Wp.ParagraphStyleId { Val = style }));
        }

        p.Append(content);
        return p;
    }

    private static Wp.Run R(string text, bool bold = false, bool italic = false)
    {
        var properties = new Wp.RunProperties();
        if (bold)
        {
            properties.Append(new Wp.Bold());
        }

        if (italic)
        {
            properties.Append(new Wp.Italic());
        }

        return new(properties, new Wp.Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Wp.Paragraph Item(int list, int level, string text) =>
        new(new Wp.ParagraphProperties(new Wp.NumberingProperties(new Wp.NumberingLevelReference { Val = level }, new Wp.NumberingId { Val = list })), R(text));

    private static Wp.TableCell Cell(string text) => new(new Wp.Paragraph(R(text)));

    /// <summary>Headings of every kind, lists, emphasis, links, tracked changes, a table and text Markdown must escape.</summary>
    private string Report() => Word("report.docx", main =>
    [
        P("Title", R("季度报告")),
        P("Heading1", R("概述")),
        P(null, R("这是"), R("重点", bold: true), R("，"), R("斜体", italic: true), R("。")),
        new Wp.Paragraph(new Wp.ParagraphProperties(new Wp.OutlineLevel { Val = 1 }), R("直接大纲")),
        P("MyHeading", R("自定义标题")),
        P(null, new Wp.Run(new Wp.RunProperties(new Wp.RunStyle { Val = "Strong" }), new Wp.Text("加粗字符样式"))),
        Item(1, 0, "苹果"),
        Item(1, 1, "红富士"),
        Item(1, 0, "香蕉"),
        Item(2, 0, "第一步"),
        Item(2, 0, "第二步"),
        P(null, R("访问 "), new Wp.Hyperlink(R("示例网站")) { Id = main.AddHyperlinkRelationship(new Uri("https://example.com/a b"), isExternal: true).Id }),
        P(null, R("保留"), new Wp.InsertedRun(R("新增")) { Id = "1", Author = "A" }, new Wp.DeletedRun(new Wp.Run(new Wp.DeletedText("删除"))) { Id = "2", Author = "A" }),
        new Wp.Table(
            new Wp.TableProperties(),
            new Wp.TableGrid(new Wp.GridColumn(), new Wp.GridColumn()),
            new Wp.TableRow(Cell("名称"), Cell("数量")),
            new Wp.TableRow(Cell("苹果"), Cell("3"))),
        P(null, new Wp.Run(new Wp.Text("第一行"), new Wp.Break(), new Wp.Text("第二行"))),
        P(null,
            new Wp.Run(new Wp.FieldChar { FieldCharType = Wp.FieldCharValues.Begin }),
            new Wp.Run(new Wp.FieldCode(" HYPERLINK \"https://example.org\" ") { Space = SpaceProcessingModeValues.Preserve }),
            new Wp.Run(new Wp.FieldChar { FieldCharType = Wp.FieldCharValues.Separate }),
            R("字段链接"),
            new Wp.Run(new Wp.FieldChar { FieldCharType = Wp.FieldCharValues.End })),
        P(null, R("# 不是标题")),
        P(null, R("1. 不是列表")),
        P(null, R("a*b_c")),
        P(null),
        P(null, new Wp.Hyperlink(R("点我")) { Id = main.AddHyperlinkRelationship(new Uri("javascript:alert(1)"), isExternal: true).Id }),
    ]);

    private Task<ConversionResult> Run(string source, string target, string? output = null) =>
        _converter.RunAsync(new([source], target, output ?? ""), new InlineProgress(_ => { }), CancellationToken.None);

    private async Task<byte[]> Convert(string source, string target, string? output = null)
    {
        var result = await Run(source, target, output);
        result.Succeeded.Should().BeTrue(result.Error);
        return File.ReadAllBytes(result.OutputPaths.Single());
    }

    private async Task<string> ConvertText(string source, string target)
    {
        var bytes = await Convert(source, target);
        bytes.Should().StartWith(Bom, "text outputs are UTF-8 with a BOM");
        return Encoding.UTF8.GetString(bytes, Bom.Length, bytes.Length - Bom.Length);
    }

    /// <summary>The paragraphs of a DOCX, after checking it against the Office 2019 schema.</summary>
    private static List<Wp.Paragraph> Paragraphs(string docx, out Wp.PageSize? size)
    {
        using var document = WordprocessingDocument.Open(docx, isEditable: false);
        new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document)
            .Select(e => $"{e.Path?.XPath}: {e.Description}").Should().BeEmpty();
        var body = document.MainDocumentPart!.Document!.Body!;
        size = (Wp.PageSize?)body.GetFirstChild<Wp.SectionProperties>()?.GetFirstChild<Wp.PageSize>()?.CloneNode(true);
        return body.Elements<Wp.Paragraph>().Select(p => (Wp.Paragraph)p.CloneNode(true)).ToList();
    }

    [Fact]
    public void Edges_are_word_to_text_markdown_and_html_and_text_to_word()
    {
        _converter.Edges.Should().BeEquivalentTo(new ConversionEdge[] { new("docx", "txt"), new("docx", "md"), new("docx", "html"), new("txt", "docx") });
    }

    [Fact]
    public void Text_to_word_is_high_and_word_out_is_medium_with_reasons()
    {
        _converter.Estimate(new(["a.txt"], "docx", ""))!.Level.Should().Be(QualityLevel.High);
        var md = _converter.Estimate(new(["a.docx"], "md", ""))!;
        md.Level.Should().Be(QualityLevel.Medium);
        md.Reasons[0].Should().Contain("标题").And.Contain("表格");
        _converter.Estimate(new(["a.docx"], "txt", ""))!.Reasons[0].Should().StartWith("只留文字");
    }

    [Fact]
    public async Task Word_to_text_has_a_line_a_paragraph_with_bullets_numbers_and_tabs()
    {
        var text = await ConvertText(Report(), "txt");

        text.Should().Be(string.Join("\r\n",
            "季度报告", "概述", "这是重点，斜体。", "直接大纲", "自定义标题", "加粗字符样式",
            "• 苹果", "  • 红富士", "• 香蕉", "1. 第一步", "2. 第二步",
            "访问 示例网站", "保留新增", "名称\t数量", "苹果\t3", "第一行", "第二行", "字段链接",
            "# 不是标题", "1. 不是列表", "a*b_c", "", "点我") + "\r\n");
    }

    [Fact]
    public async Task Word_to_markdown_keeps_headings_lists_emphasis_links_and_tables()
    {
        var bytes = await Convert(Report(), "md");

        bytes.Take(Bom.Length).Should().NotEqual(Bom, "Markdown tools expect UTF-8 without a BOM");
        Encoding.UTF8.GetString(bytes).Should().Be("""
            # 季度报告

            # 概述

            这是**重点**，*斜体*。

            ## 直接大纲

            # 自定义标题

            **加粗字符样式**

            - 苹果
                - 红富士
            - 香蕉

            1. 第一步
            2. 第二步

            访问 [示例网站](https://example.com/a%20b)

            保留新增

            | 名称 | 数量 |
            | --- | --- |
            | 苹果 | 3 |

            第一行\
            第二行

            [字段链接](https://example.org)

            \# 不是标题

            1\. 不是列表

            a\*b\_c

            点我

            """);
    }

    [Fact]
    public async Task Word_to_html_is_a_page_with_nested_lists_tables_and_safe_links()
    {
        var bytes = await Convert(Report(), "html");

        var html = Encoding.UTF8.GetString(bytes);
        html.Should().StartWith("<!DOCTYPE html>").And.Contain("<meta charset=\"utf-8\">").And.Contain("<title>report</title>");
        html.Should().Contain("<h1>季度报告</h1>").And.Contain("<h2>直接大纲</h2>").And.Contain("<h1>自定义标题</h1>");
        html.Should().Contain("<p>这是<strong>重点</strong>，<em>斜体</em>。</p>");
        html.Should().Contain("<ul>\n<li>苹果<ul>\n<li>红富士</li>\n</ul>\n</li>\n<li>香蕉</li>\n</ul>\n<ol>\n<li>第一步</li>\n<li>第二步</li>\n</ol>\n");
        html.Should().MatchRegex("<p>访问 <a href=\"https://example.com/a(%20| )b\">示例网站</a></p>");
        html.Should().Contain("<table>\n<tr>\n<td>\n<p>名称</p>\n</td>");
        html.Should().Contain("<p>第一行<br>\n第二行</p>").And.Contain("<a href=\"https://example.org\">字段链接</a>");
        html.Should().Contain("<p>点我</p>").And.NotContain("javascript");
    }

    [Fact]
    public async Task A_list_counting_from_three_keeps_its_first_number()
    {
        var source = Word("steps.docx", _ => [P(null, R("步骤")), Item(1, 0, "准备"), Item(3, 0, "甲"), Item(3, 0, "乙")]);

        Encoding.UTF8.GetString(await Convert(source, "md")).Should().Be("步骤\n\n- 准备\n\n3. 甲\n4. 乙\n");
        Encoding.UTF8.GetString(await Convert(source, "html")).Should().Contain("<ol start=\"3\">\n<li>甲</li>\n<li>乙</li>\n</ol>");
    }

    [Fact]
    public async Task A_text_box_is_read_once_after_its_paragraph()
    {
        // Word writes a text box twice: DrawingML in the choice, VML in the fallback.
        const string Box = "<w:txbxContent><w:p><w:r><w:t>文本框里的字</w:t></w:r></w:p></w:txbxContent>";
        var source = Package("box.docx", $"""
            <w:document xmlns:w="{W}" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape" xmlns:v="urn:schemas-microsoft-com:vml" mc:Ignorable="wps">
            <w:body><w:p><w:r><w:t>正文</w:t></w:r><w:r><mc:AlternateContent>
            <mc:Choice Requires="wps"><w:drawing><wps:wsp><wps:txbx>{Box}</wps:txbx></wps:wsp></w:drawing></mc:Choice>
            <mc:Fallback><w:pict><v:shape><v:textbox>{Box}</v:textbox></v:shape></w:pict></mc:Fallback>
            </mc:AlternateContent></w:r></w:p></w:body></w:document>
            """);

        (await ConvertText(source, "txt")).Should().Be("正文\r\n文本框里的字\r\n");
    }

    /// <summary>A DOCX with only the parts a reader needs, document.xml as given.</summary>
    private string Package(string name, string document)
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
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>
            """);
        Entry("_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
            """);
        Entry("word/document.xml", document);
        return path;
    }

    [Fact]
    public async Task A_password_protected_document_is_refused()
    {
        var source = Path.Combine(_dir, "secret.docx");
        File.WriteAllBytes(source, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);

        var result = await Run(source, "md");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("secret.docx 有密码保护，或是改了扩展名的旧版 .doc，暂不支持");
    }

    [Fact]
    public async Task A_file_that_is_not_a_zip_is_refused()
    {
        var source = Path.Combine(_dir, "fake.docx");
        File.WriteAllText(source, "hello");

        (await Run(source, "txt")).Error.Should().Be("fake.docx 不是有效的 DOCX 文件");
    }

    [Fact]
    public async Task Broken_xml_is_reported_as_unreadable()
    {
        var source = Package("broken.docx", $"<w:document xmlns:w=\"{W}\"><w:body>");

        (await Run(source, "html")).Error.Should().StartWith("broken.docx 无法读取：");
    }

    [Fact]
    public async Task A_document_without_text_writes_nothing()
    {
        var source = Word("empty.docx", _ => [P(null), P(null, R("  "))]);

        var result = await Run(source, "md");

        result.Error.Should().Be("empty.docx 里没有文字");
        Directory.GetFiles(_dir).Should().ContainSingle();
    }

    [Fact]
    public async Task Gbk_text_becomes_an_a4_word_document_with_a_paragraph_a_line()
    {
        var source = Path.Combine(_dir, "notes.txt");
        File.WriteAllBytes(source, Encoding.GetEncoding("GBK").GetBytes("第一行\r\n名称\t数量\r\n\r\n最后一行\r\n"));

        var result = await Run(source, "docx");

        result.Succeeded.Should().BeTrue(result.Error);
        var paragraphs = Paragraphs(result.OutputPaths.Single(), out var size);
        paragraphs.Select(p => p.InnerText).Should().Equal("第一行", "名称数量", "", "最后一行");
        paragraphs[1].Descendants<Wp.TabChar>().Should().ContainSingle();
        size!.Width!.Value.Should().Be(11906u);
        size.Height!.Value.Should().Be(16838u);
    }

    [Fact]
    public async Task Text_survives_a_round_trip_through_word()
    {
        const string Text = "Hello\tworld\r\n\r\n中文 ✓\r\n";
        var source = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(source, Text, new UTF8Encoding(false));
        var docx = (await Run(source, "docx")).OutputPaths.Single();
        var back = Directory.CreateTempSubdirectory("dropwheel-document-back-").FullName;
        try
        {
            var bytes = await Convert(docx, "txt", back);

            Encoding.UTF8.GetString(bytes, Bom.Length, bytes.Length - Bom.Length).Should().Be(Text);
        }
        finally
        {
            Directory.Delete(back, recursive: true);
        }
    }

    [Fact]
    public async Task Empty_text_is_refused()
    {
        var source = Path.Combine(_dir, "blank.txt");
        File.WriteAllText(source, "\r\n  \r\n");

        (await Run(source, "docx")).Error.Should().Be("blank.txt 里没有文字");
    }

    [Fact]
    public async Task Other_targets_are_refused()
    {
        (await Run(Report(), "pdf")).Error.Should().Be("不支持输出 pdf");
    }
}
