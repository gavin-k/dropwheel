using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Graphics.Colors;

namespace DropWheel.Engines.Pdf;

/// <summary>
/// A PDF rebuilt as an editable DOCX from its text layer. PdfPig cuts each page into text blocks (Docstrum) and
/// puts them in reading order; each block becomes a paragraph, or several where the text size changes between
/// lines, that keeps the size, bold, italic and colour of its words in the document's default font. Images go in
/// before the first paragraph below them: JPEG data unchanged, others as PNG when PdfPig can decode them. Each PDF
/// page starts a new page. Columns, tables, headers and footers come out as plain paragraphs. A scanned PDF has no
/// text layer and fails, as it does for TXT; an OCR'd scan keeps its invisible text and drops the page image under it.
///
/// The package is written by hand: the few WordprocessingML elements this needs do not justify the 8.5 MB
/// Open XML SDK, which the tests use to validate the output instead.
/// </summary>
internal static class PdfDocx
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    private const long EmuPerPoint = 12700;
    private const int TwipsPerPoint = 20;

    /// <summary>An image covering this share of a page that has text is taken for a scan under OCR text.</summary>
    private const double ScanCoverage = 0.8;

    private const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="jpeg" ContentType="image/jpeg"/><Default Extension="png" ContentType="image/png"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/></Types>
        """;

    private const string PackageRelationships = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
        """;

    /// <summary>Calibri and DengXian, the Office defaults for Western and Chinese text; 6 pt after each paragraph.</summary>
    private const string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:eastAsia="DengXian" w:hAnsi="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:szCs w:val="22"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="120"/></w:pPr></w:pPrDefault></w:docDefaults><w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style></w:styles>
        """;

    private static readonly XmlWriterSettings Settings = new() { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) };

    private readonly record struct Look(double Size, bool Bold, bool Italic, string? Color);

    private sealed record Picture(string Extension, byte[] Data, PdfRectangle Bounds);

    public static Task<string?> WriteAsync(
        ConversionRequest request,
        string source,
        int index,
        DateTimeOffset now,
        IProgress<double> progress,
        CancellationToken cancellationToken) =>
        OutputFiles.WriteAsync(request, source, index, now, temp =>
        {
            Write(source, temp, progress, cancellationToken);
            return Task.CompletedTask;
        });

    private static void Write(string source, string temp, IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var pdf = PdfDocument.Open(source);
        using var zip = new ZipArchive(File.Create(temp), ZipArchiveMode.Create);
        WriteText(zip, "[Content_Types].xml", ContentTypes);
        WriteText(zip, "_rels/.rels", PackageRelationships);

        // The pictures go into the package page by page; the text waits here until the last page is read.
        var pictures = new List<string>();
        var text = new MemoryStream();
        var words = 0;
        using (var xml = XmlWriter.Create(text, Settings))
        {
            xml.WriteStartElement("w", "document", W);
            foreach (var (prefix, ns) in new[] { ("r", R), ("wp", WP), ("a", A), ("pic", Pic) })
            {
                xml.WriteAttributeString("xmlns", prefix, null, ns);
            }

            xml.WriteStartElement("w", "body", W);
            var first = pdf.GetPage(1);
            var margin = Math.Min(72, Math.Min(first.Width, first.Height) / 8);
            var room = (Width: first.Width - 2 * margin, Height: first.Height - 2 * margin);
            for (var number = 1; number <= pdf.NumberOfPages; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                words += WritePage(xml, zip, pdf.GetPage(number), newPage: number > 1, pictures, room);
                progress.Report((double)number / pdf.NumberOfPages);
            }

            if (words == 0)
            {
                throw new InvalidDataException($"{Path.GetFileName(source)} 没有可提取的文字（可能是扫描件，需要 OCR）");
            }

            WriteSection(xml, first.Width, first.Height, margin);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        using (var entry = zip.CreateEntry("word/document.xml").Open())
        {
            text.Position = 0;
            text.CopyTo(entry);
        }

        WriteText(zip, "word/styles.xml", Styles);
        var relationships = new StringBuilder("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="styles" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            """);
        for (var i = 0; i < pictures.Count; i++)
        {
            relationships.Append(CultureInfo.InvariantCulture,
                $"""<Relationship Id="image{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/{pictures[i]}"/>""");
        }

        WriteText(zip, "word/_rels/document.xml.rels", relationships.Append("</Relationships>").ToString());
    }

    /// <returns>How many words the page has.</returns>
    private static int WritePage(XmlWriter xml, ZipArchive zip, Page page, bool newPage, List<string> pictures, (double Width, double Height) room)
    {
        var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).Where(w => Clean(w.Text).Trim().Length > 0).ToList();
        var blocks = words.Count == 0 ? [] : UnsupervisedReadingOrderDetector.Instance.Get(DocstrumBoundingBoxes.Instance.GetBlocks(words)).ToList();
        var crop = page.CropBox.Bounds;
        var images = page.GetImages()
            .Where(i => !i.IsImageMask && i.BoundingBox.Width >= 2 && i.BoundingBox.Height >= 2)
            .Where(i => words.Count == 0 || i.BoundingBox.Area < ScanCoverage * crop.Area)
            .Select(Encode).OfType<Picture>()
            .OrderByDescending(p => p.Bounds.Top)
            .ToList();

        var pageBreak = newPage;
        var empty = true;
        void Paragraph(Action<XmlWriter> content, bool centered = false)
        {
            xml.WriteStartElement("w", "p", W);
            if (pageBreak || centered)
            {
                xml.WriteStartElement("w", "pPr", W);
                if (pageBreak)
                {
                    xml.WriteElementString("w", "pageBreakBefore", W, null);
                }

                if (centered)
                {
                    xml.WriteStartElement("w", "jc", W);
                    xml.WriteAttributeString("w", "val", W, "center");
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }

            content(xml);
            xml.WriteEndElement();
            pageBreak = false;
            empty = false;
        }

        void Image(Picture picture)
        {
            var name = $"image{pictures.Count + 1}.{picture.Extension}";
            using (var entry = zip.CreateEntry($"word/media/{name}", CompressionLevel.NoCompression).Open())
            {
                entry.Write(picture.Data);
            }

            pictures.Add(name);
            var scale = Math.Min(1, Math.Min(room.Width / picture.Bounds.Width, room.Height / picture.Bounds.Height));
            Paragraph(x => WriteDrawing(x, pictures.Count, name, (long)(picture.Bounds.Width * scale * EmuPerPoint), (long)(picture.Bounds.Height * scale * EmuPerPoint)));
        }

        foreach (var paragraph in blocks.SelectMany(Paragraphs))
        {
            var (top, left, right) = (paragraph.Max(w => w.BoundingBox.Top), paragraph.Min(w => w.BoundingBox.Left), paragraph.Max(w => w.BoundingBox.Right));
            foreach (var picture in images.Where(p => p.Bounds.Top >= top - 1 && p.Bounds.Left < right && left < p.Bounds.Right).ToList())
            {
                Image(picture);
                images.Remove(picture);
            }

            Paragraph(x => WriteRuns(x, paragraph), Centered(paragraph, left, right, crop));
        }

        images.ForEach(Image);
        if (empty)
        {
            Paragraph(_ => { });
        }

        return words.Count;
    }

    /// <summary>JPEG data goes in as it is; anything else PdfPig can decode becomes PNG; JPEG 2000, JBIG2 and CCITT are dropped.</summary>
    private static Picture? Encode(IPdfImage image) =>
        image.RawMemory.Span is [0xFF, 0xD8, 0xFF, ..] ? new("jpeg", image.RawMemory.ToArray(), image.BoundingBox)
        : image.TryGetPng(out var png) ? new("png", png, image.BoundingBox)
        : null;

    /// <summary>
    /// A block cut where the text size changes from one line to the next, so a heading set close above its text
    /// still gets a paragraph of its own.
    /// </summary>
    private static IEnumerable<List<Word>> Paragraphs(TextBlock block)
    {
        var paragraph = new List<Word>();
        foreach (var word in block.TextLines.SelectMany(l => l.Words))
        {
            if (paragraph is [.., var previous] && !SameLine(previous, word)
                && Math.Abs(LookOf(previous).Size - LookOf(word).Size) > Math.Max(LookOf(previous).Size, LookOf(word).Size) * 0.1)
            {
                yield return paragraph;
                paragraph = [];
            }

            paragraph.Add(word);
        }

        if (paragraph.Count > 0)
        {
            yield return paragraph;
        }
    }

    /// <summary>A short paragraph in the middle of the page, such as a title.</summary>
    private static bool Centered(List<Word> paragraph, double left, double right, PdfRectangle crop) =>
        paragraph.Zip(paragraph.Skip(1)).Count(pair => !SameLine(pair.First, pair.Second)) < 3 && right - left < crop.Width * 0.6
            && Math.Abs((left + right) / 2 - (crop.Left + crop.Right) / 2) < crop.Width * 0.05;

    private static void WriteRuns(XmlWriter xml, List<Word> paragraph)
    {
        var run = new StringBuilder();
        Look? look = null;
        Word? previous = null;
        foreach (var word in paragraph)
        {
            var separator = previous is null ? "" : Separator(previous, word);
            var next = LookOf(word);
            if (look is { } current && current != next)
            {
                WriteRun(xml, current, run.Append(separator).ToString());
                run.Clear();
                separator = "";
            }

            run.Append(separator).Append(Clean(word.Text));
            look = next;
            previous = word;
        }

        if (look is { } last)
        {
            WriteRun(xml, last, run.ToString());
        }
    }

    /// <summary>
    /// What goes between two words of a paragraph, by where they sit rather than by Docstrum's lines, which can
    /// merge lines when a page has few words. On one line: a space when there is a gap, none when only the font
    /// changed. Across lines: a space, except after a hyphen and next to Chinese, Japanese or Korean text.
    /// </summary>
    private static string Separator(Word previous, Word next)
    {
        if (SameLine(previous, next))
        {
            return next.BoundingBox.Left - previous.BoundingBox.Right > Math.Max(previous.Letters[^1].PointSize, 1) * 0.1 ? " " : "";
        }

        var (end, start) = (previous.Text.TrimEnd()[^1], next.Text.TrimStart()[0]);
        return end == '-' || IsCjk(end) || IsCjk(start) ? "" : " ";
    }

    /// <summary>On the same baseline, give or take half the text size, and not back at the left of it.</summary>
    private static bool SameLine(Word previous, Word next)
    {
        var size = Math.Max(previous.Letters[^1].PointSize, 1);
        return Math.Abs(previous.Letters[^1].StartBaseLine.Y - next.Letters[0].StartBaseLine.Y) < size / 2
            && next.BoundingBox.Left - previous.BoundingBox.Right > -size / 2;
    }

    private static bool IsCjk(char c) => c is (>= '⺀' and <= '鿿') or (>= '가' and <= '힯') or (>= '豈' and <= '﫿') or (>= '＀' and <= '￯');

    private static Look LookOf(Word word)
    {
        var letter = word.Letters[0];
        var sizes = word.Letters.Select(l => l.PointSize).Order().ToList();
        var size = Math.Clamp(Math.Round(sizes[sizes.Count / 2] * 2, MidpointRounding.AwayFromZero) / 2, 1, 1638);
        var name = letter.FontName ?? "";
        bool Named(params string[] parts) => parts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
        var bold = letter.FontDetails?.IsBold == true || Named("Bold", "Black", "Heavy");
        var italic = letter.FontDetails?.IsItalic == true || Named("Italic", "Oblique");
        return new(size, bold, italic, ColorOf(letter.Color));
    }

    /// <summary>RGB hex, or null for black, and for near white, which would vanish on a white page without its box.</summary>
    private static string? ColorOf(IColor? color)
    {
        if (color is null or PatternColor)
        {
            return null;
        }

        var (r, g, b) = color.ToRGBValues();
        var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return luminance < 0.1 || luminance > 0.85 ? null
            : string.Create(CultureInfo.InvariantCulture, $"{Channel(r):X2}{Channel(g):X2}{Channel(b):X2}");
    }

    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    private static void WriteRun(XmlWriter xml, Look look, string text)
    {
        xml.WriteStartElement("w", "r", W);
        xml.WriteStartElement("w", "rPr", W);
        if (look.Bold)
        {
            xml.WriteElementString("w", "b", W, null);
        }

        if (look.Italic)
        {
            xml.WriteElementString("w", "i", W, null);
        }

        if (look.Color is { } color)
        {
            Value(xml, "color", color);
        }

        var halfPoints = ((int)(look.Size * 2)).ToString(CultureInfo.InvariantCulture);
        Value(xml, "sz", halfPoints);
        Value(xml, "szCs", halfPoints);
        xml.WriteEndElement();
        xml.WriteStartElement("w", "t", W);
        xml.WriteAttributeString("xml", "space", null, "preserve");
        xml.WriteString(text);
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void WriteDrawing(XmlWriter xml, int id, string name, long cx, long cy)
    {
        xml.WriteStartElement("w", "r", W);
        xml.WriteStartElement("w", "drawing", W);
        xml.WriteStartElement("wp", "inline", WP);
        xml.WriteStartElement("wp", "extent", WP);
        Attributes(xml, ("cx", cx), ("cy", cy));
        xml.WriteEndElement();
        xml.WriteStartElement("wp", "docPr", WP);
        Attributes(xml, ("id", id), ("name", name));
        xml.WriteEndElement();
        xml.WriteStartElement("a", "graphic", A);
        xml.WriteStartElement("a", "graphicData", A);
        xml.WriteAttributeString("uri", Pic);
        xml.WriteStartElement("pic", "pic", Pic);
        xml.WriteStartElement("pic", "nvPicPr", Pic);
        xml.WriteStartElement("pic", "cNvPr", Pic);
        Attributes(xml, ("id", id), ("name", name));
        xml.WriteEndElement();
        xml.WriteElementString("pic", "cNvPicPr", Pic, null);
        xml.WriteEndElement();
        xml.WriteStartElement("pic", "blipFill", Pic);
        xml.WriteStartElement("a", "blip", A);
        xml.WriteAttributeString("r", "embed", R, $"image{id}");
        xml.WriteEndElement();
        xml.WriteStartElement("a", "stretch", A);
        xml.WriteElementString("a", "fillRect", A, null);
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteStartElement("pic", "spPr", Pic);
        xml.WriteStartElement("a", "xfrm", A);
        xml.WriteStartElement("a", "off", A);
        Attributes(xml, ("x", 0), ("y", 0));
        xml.WriteEndElement();
        xml.WriteStartElement("a", "ext", A);
        Attributes(xml, ("cx", cx), ("cy", cy));
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteStartElement("a", "prstGeom", A);
        xml.WriteAttributeString("prst", "rect");
        xml.WriteElementString("a", "avLst", A, null);
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    /// <summary>The page size of the first page, landscape when it is wider than tall.</summary>
    private static void WriteSection(XmlWriter xml, double width, double height, double margin)
    {
        static string Twips(double points) => ((int)Math.Round(points * TwipsPerPoint)).ToString(CultureInfo.InvariantCulture);
        xml.WriteStartElement("w", "sectPr", W);
        xml.WriteStartElement("w", "pgSz", W);
        xml.WriteAttributeString("w", "w", W, Twips(width));
        xml.WriteAttributeString("w", "h", W, Twips(height));
        if (width > height)
        {
            xml.WriteAttributeString("w", "orient", W, "landscape");
        }

        xml.WriteEndElement();
        xml.WriteStartElement("w", "pgMar", W);
        foreach (var side in new[] { "top", "right", "bottom", "left" })
        {
            xml.WriteAttributeString("w", side, W, Twips(margin));
        }

        foreach (var side in new[] { "header", "footer" })
        {
            xml.WriteAttributeString("w", side, W, Twips(margin / 2));
        }

        xml.WriteAttributeString("w", "gutter", W, "0");
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void Value(XmlWriter xml, string element, string value)
    {
        xml.WriteStartElement("w", element, W);
        xml.WriteAttributeString("w", "val", W, value);
        xml.WriteEndElement();
    }

    private static void Attributes(XmlWriter xml, params (string Name, object Value)[] attributes)
    {
        foreach (var (name, value) in attributes)
        {
            xml.WriteAttributeString(name, Convert.ToString(value, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Text layers often carry control characters from a bad ToUnicode map; XML 1.0 has no place for them.</summary>
    private static string Clean(string text)
    {
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
                clean.Append(c is '\t' or '\r' or '\n' ? ' ' : c);
            }
        }

        return clean.ToString();
    }

    private static void WriteText(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Settings.Encoding);
        writer.Write(content);
    }
}
