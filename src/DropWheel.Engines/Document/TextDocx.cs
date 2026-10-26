using System.IO.Compression;
using System.Xml;

namespace DropWheel.Engines.Document;

/// <summary>A text file as a DOCX: a paragraph a line, tabs kept, in the Office default fonts on an A4 page.</summary>
internal static class TextDocx
{
    private const string W = DocxPackage.W;

    /// <param name="lines">The text split at its line breaks, without the empty line after a final break.</param>
    public static void Write(IReadOnlyList<string> lines, string temp, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(File.Create(temp), ZipArchiveMode.Create);
        DocxPackage.WriteText(zip, "[Content_Types].xml", DocxPackage.ContentTypes);
        DocxPackage.WriteText(zip, "_rels/.rels", DocxPackage.PackageRelationships);
        using (var entry = zip.CreateEntry("word/document.xml").Open())
        using (var xml = XmlWriter.Create(entry, DocxPackage.Settings))
        {
            xml.WriteStartElement("w", "document", W);
            xml.WriteStartElement("w", "body", W);
            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                xml.WriteStartElement("w", "p", W);
                if (line.Length > 0)
                {
                    xml.WriteStartElement("w", "r", W);
                    var parts = line.Split('\t');
                    for (var i = 0; i < parts.Length; i++)
                    {
                        if (i > 0)
                        {
                            xml.WriteStartElement("w", "tab", W);
                            xml.WriteEndElement();
                        }

                        if (parts[i].Length > 0)
                        {
                            xml.WriteStartElement("w", "t", W);
                            xml.WriteAttributeString("xml", "space", null, "preserve");
                            xml.WriteString(DocxPackage.Clean(parts[i]));
                            xml.WriteEndElement();
                        }
                    }

                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }

            // A4 with Word's default one-inch margins.
            DocxPackage.WriteSection(xml, 595.3, 841.9, 72);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        DocxPackage.WriteText(zip, "word/styles.xml", DocxPackage.Styles(after: 0));
        DocxPackage.WriteText(zip, "word/_rels/document.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="styles" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
            """);
    }
}
