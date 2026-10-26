using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace DropWheel.Engines.Document;

/// <summary>
/// What every DOCX this app writes by hand shares: the content types, the package relationship, a style sheet
/// in the Office default fonts, the section and a few writer helpers. The few WordprocessingML elements these
/// need do not justify the 8.5 MB Open XML SDK, which the tests use to validate the output instead.
/// </summary>
internal static class DocxPackage
{
    public const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="jpeg" ContentType="image/jpeg"/><Default Extension="png" ContentType="image/png"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/></Types>
        """;

    public const string PackageRelationships = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
        """;

    public static readonly XmlWriterSettings Settings = new() { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) };

    /// <summary>Calibri and DengXian, the Office defaults for Western and Chinese text, 11 pt, <paramref name="after"/> twips after each paragraph.</summary>
    public static string Styles(int after) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:eastAsia="DengXian" w:hAnsi="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:szCs w:val="22"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="{after}"/></w:pPr></w:pPrDefault></w:docDefaults><w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style></w:styles>
        """;

    /// <summary>A page <paramref name="width"/> by <paramref name="height"/> points, landscape when it is wider than tall.</summary>
    public static void WriteSection(XmlWriter xml, double width, double height, double margin)
    {
        static string Twips(double points) => ((int)Math.Round(points * 20)).ToString(CultureInfo.InvariantCulture);
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

    public static void Value(XmlWriter xml, string element, string value)
    {
        xml.WriteStartElement("w", element, W);
        xml.WriteAttributeString("w", "val", W, value);
        xml.WriteEndElement();
    }

    /// <summary>
    /// Text with what XML 1.0 has no place for taken out, such as the control characters a PDF's bad ToUnicode
    /// map leaves; tabs and line breaks become spaces, since a run has elements of its own for them.
    /// </summary>
    public static string Clean(string text)
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

    public static void WriteText(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Settings.Encoding);
        writer.Write(content);
    }
}
