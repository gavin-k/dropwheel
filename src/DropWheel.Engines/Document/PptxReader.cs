using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace DropWheel.Engines.Document;

/// <summary>
/// The text of a PPTX as <see cref="DocBlock"/>s for <see cref="DocxExport"/>. Each slide shown in the slide show,
/// in order: its title as a heading, "幻灯片 N" when it has none; then the text of its shapes in the order they are
/// stacked, groups opened, with the bullets and numbers PowerPoint shows; tables; the text of SmartArt; and the
/// speaker notes under a "备注" heading. Hidden slides, dates, footers, slide numbers, pictures and charts are left
/// out, and so is everything the layouts and masters draw. Hand-parsed, through <see cref="OoxmlPackage"/>.
/// </summary>
internal sealed class PptxReader
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = DocxPackage.R;
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace Dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";

    private const string TableUri = "http://schemas.openxmlformats.org/drawingml/2006/table";
    private const string DiagramUri = "http://schemas.openxmlformats.org/drawingml/2006/diagram";

    /// <summary>Placeholders for what PowerPoint repeats on every slide rather than what the slide says.</summary>
    private static readonly string[] Furniture = ["dt", "ftr", "sldNum", "hdr"];

    private enum Mark { None, Bullet, Number }

    /// <param name="Start">The first number of a numbered list.</param>
    private readonly record struct Marker(Mark Mark, int Start = 1);

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, XDocument?> _parts = [];

    private PptxReader(ZipArchive zip) => _zip = zip;

    /// <summary>The blocks of every shown slide, or none when not one of them has any text.</summary>
    /// <exception cref="InvalidDataException">The file is encrypted, an old .ppt, damaged, or not a presentation; the message says which for the user.</exception>
    public static IReadOnlyList<DocBlock> Read(string path, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(path);
        using var zip = OoxmlPackage.Open(path, "PPTX", ".ppt");
        var reader = new PptxReader(zip);
        XDocument? presentation;
        Dictionary<string, string> slides;
        try
        {
            var main = OoxmlPackage.Main(zip, "ppt/presentation.xml");
            presentation = reader.Part(main);
            slides = OoxmlPackage.Relationships(zip, main).Where(r => !r.External).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Target);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException)
        {
            throw Damaged(name, ex);
        }

        if (presentation?.Root?.Name != P + "presentation")
        {
            throw new InvalidDataException(L.T("{0} 不是 PowerPoint 演示文稿，或是 Strict Open XML 格式，暂不支持", name));
        }

        var blocks = new List<DocBlock>();
        var any = false;
        var number = 0;
        try
        {
            foreach (var id in presentation.Root.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                number++;
                if (id.Attribute(R + "id")?.Value is { } rel && slides.TryGetValue(rel, out var part))
                {
                    any |= reader.Slide(part, number, blocks);
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException)
        {
            throw Damaged(name, ex);
        }

        return any ? blocks : [];
    }

    /// <summary>For a part that is not well-formed XML, or an entry the zip reader cannot inflate.</summary>
    private static InvalidDataException Damaged(string name, Exception ex) => new(L.T("{0} 无法读取：{1}", name, ex.Message), ex);

    /// <summary>Adds the slide's blocks, after a blank line when it is not the first; whether it has any text.</summary>
    private bool Slide(string part, int number, List<DocBlock> blocks)
    {
        if (Part(part)?.Root is not { } slide || (string?)slide.Attribute("show") is "0" or "false")
        {
            return false;
        }

        var rels = OoxmlPackage.Relationships(_zip, part);
        var links = rels.Where(r => r.External).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Target);
        var layout = Related(rels, "/slideLayout");
        var master = layout is null ? null : Related(OoxmlPackage.Relationships(_zip, layout), "/slideMaster");
        var shapes = Shapes(slide.Element(P + "cSld")?.Element(P + "spTree")).ToList();

        var title = shapes.FirstOrDefault(s => Placeholder(s)?.Type is "title" or "ctrTitle");
        var heading = new List<DocRun>();
        foreach (var runs in (title?.Element(P + "txBody")?.Elements(A + "p") ?? []).Select(p => Runs(p, links)).Where(HasText))
        {
            if (heading.Count > 0)
            {
                heading.Add(new DocRun("\n", false, false, null));
            }

            heading.AddRange(runs);
        }

        var content = new List<DocBlock>();
        foreach (var shape in shapes.Where(s => s != title))
        {
            var placeholder = Placeholder(shape);
            if (shape.Name == P + "sp" && !Furniture.Contains(placeholder?.Type))
            {
                content.AddRange(Paragraphs(shape.Element(P + "txBody"), placeholder is { } ph ? Styles(ph, layout, master) : [], links));
            }
            else if (shape.Name == P + "graphicFrame" && shape.Element(A + "graphic")?.Element(A + "graphicData") is { } data)
            {
                var uri = (string?)data.Attribute("uri");
                if (uri == TableUri && data.Element(A + "tbl") is { } tbl && Table(tbl, links) is var table && DocxReader.HasText([table]))
                {
                    content.Add(table);
                }
                else if (uri == DiagramUri && data.Element(Dgm + "relIds")?.Attribute(R + "dm")?.Value is { } dm
                    && rels.FirstOrDefault(r => r.Id == dm && !r.External).Target is { } target)
                {
                    content.AddRange(Diagram(target));
                }
            }
        }

        var notes = new List<DocBlock>();
        if (Related(rels, "/notesSlide") is { } notesPart && Part(notesPart)?.Root is { } notesSlide)
        {
            var notesLinks = OoxmlPackage.Relationships(_zip, notesPart).Where(r => r.External).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Target);
            foreach (var shape in Shapes(notesSlide.Element(P + "cSld")?.Element(P + "spTree")).Where(s => Placeholder(s)?.Type == "body"))
            {
                notes.AddRange(Paragraphs(shape.Element(P + "txBody"), [], notesLinks));
            }
        }

        var titled = heading.Count > 0;
        if (blocks.Count > 0)
        {
            blocks.Add(new DocParagraph(0, null, []));
        }

        blocks.Add(new DocParagraph(1, null, titled ? DocxReader.Merge(heading) : [new DocRun(L.T("幻灯片 {0}", number), false, false, null)]));
        blocks.AddRange(content);
        var noted = DocxReader.HasText(notes);
        if (noted)
        {
            blocks.Add(new DocParagraph(2, null, [new DocRun(L.T("备注"), false, false, null)]));
            blocks.AddRange(notes);
        }

        return titled || noted || DocxReader.HasText(content);
    }

    /// <summary>The shapes that may hold text, in the order they are stacked, groups opened, the first choice of alternate content.</summary>
    private static IEnumerable<XElement> Shapes(XElement? tree)
    {
        foreach (var e in tree?.Elements() ?? [])
        {
            var inner = e.Name == P + "grpSp" ? e
                : e.Name == Mc + "AlternateContent" ? e.Element(Mc + "Choice") ?? e.Element(Mc + "Fallback")
                : null;
            if (inner is not null)
            {
                foreach (var shape in Shapes(inner))
                {
                    yield return shape;
                }
            }
            else if (e.Name == P + "sp" || e.Name == P + "graphicFrame")
            {
                yield return e;
            }
        }
    }

    /// <summary>The placeholder a shape is, with the type and index the schema gives one that leaves them out.</summary>
    private static (string Type, string Index)? Placeholder(XElement shape) =>
        shape.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("nv", StringComparison.Ordinal))?.Element(P + "nvPr")?.Element(P + "ph") is { } ph
            ? ((string?)ph.Attribute("type") ?? "obj", (string?)ph.Attribute("idx") ?? "0")
            : null;

    /// <summary>
    /// Where a placeholder's paragraphs get the bullets they do not set themselves, nearest first: the placeholder
    /// it follows on the layout (of the same kind, the same index if there is one), the one on the master, then the
    /// master's text style for titles, body text or the rest.
    /// </summary>
    private List<XElement> Styles((string Type, string Index) placeholder, string? layout, string? master)
    {
        var styles = new List<XElement>();
        var kind = Base(placeholder.Type);
        var onLayout = Placeholders(layout).Select(s => (Shape: s, Placeholder: Placeholder(s)!.Value)).Where(s => Base(s.Placeholder.Type) == kind).ToList();
        var follows = onLayout.FirstOrDefault(s => s.Placeholder.Index == placeholder.Index).Shape ?? onLayout.FirstOrDefault().Shape;
        if (follows?.Element(P + "txBody")?.Element(A + "lstStyle") is { } own)
        {
            styles.Add(own);
        }

        if (Placeholders(master).FirstOrDefault(s => Placeholder(s)?.Type == kind)?.Element(P + "txBody")?.Element(A + "lstStyle") is { } onMaster)
        {
            styles.Add(onMaster);
        }

        var text = kind switch { "title" => "titleStyle", "body" => "bodyStyle", _ => "otherStyle" };
        if (Part(master ?? "")?.Root?.Element(P + "txStyles")?.Element(P + text) is { } style)
        {
            styles.Add(style);
        }

        return styles;
    }

    private IEnumerable<XElement> Placeholders(string? part) =>
        part is null ? [] : Shapes(Part(part)?.Root?.Element(P + "cSld")?.Element(P + "spTree")).Where(s => Placeholder(s) is not null);

    /// <summary>The placeholder on the master that one of <paramref name="type"/> follows.</summary>
    private static string Base(string type) => type switch
    {
        "title" or "ctrTitle" => "title",
        "dt" or "ftr" or "sldNum" or "hdr" => type,
        _ => "body",
    };

    /// <summary>
    /// The paragraphs of a text body. A paragraph's own properties, then those for its level in the body's list
    /// style, then in <paramref name="styles"/>, say whether it has a bullet or a number; numbers count up within the
    /// body, from the start a level gives, and start over after a paragraph of that level without one.
    /// </summary>
    private static List<DocBlock> Paragraphs(XElement? body, List<XElement> styles, Dictionary<string, string> links)
    {
        var blocks = new List<DocBlock>();
        if (body is null)
        {
            return blocks;
        }

        List<XElement> chain = [.. body.Elements(A + "lstStyle"), .. styles];
        var counters = new int[9];
        foreach (var p in body.Elements(A + "p"))
        {
            var runs = Runs(p, links);
            if (!HasText(runs))
            {
                continue;
            }

            var properties = p.Element(A + "pPr");
            var level = Math.Clamp(int.TryParse((string?)properties?.Attribute("lvl"), out var lvl) ? lvl : 0, 0, 8);
            var marker = MarkerOf(properties)
                ?? chain.Select(s => MarkerOf(s.Element(A + $"lvl{level + 1}pPr"))).FirstOrDefault(m => m is not null)
                ?? new Marker(Mark.None);
            Array.Clear(counters, level + 1, counters.Length - level - 1);
            DocListItem? item = null;
            if (marker.Mark == Mark.Number)
            {
                counters[level] = counters[level] > 0 ? counters[level] + 1 : marker.Start;
                item = new DocListItem(level, Ordered: true, counters[level]);
            }
            else
            {
                counters[level] = 0;
                item = marker.Mark == Mark.Bullet ? new DocListItem(level, Ordered: false, 0) : null;
            }

            blocks.Add(new DocParagraph(0, item, DocxReader.Merge(runs)));
        }

        return blocks;
    }

    private static Marker? MarkerOf(XElement? properties)
    {
        if (properties is null)
        {
            return null;
        }

        if (properties.Element(A + "buNone") is not null)
        {
            return new Marker(Mark.None);
        }

        if (properties.Element(A + "buAutoNum") is { } number)
        {
            return new Marker(Mark.Number, int.TryParse((string?)number.Attribute("startAt"), out var start) && start > 0 ? start : 1);
        }

        return properties.Element(A + "buChar") is not null || properties.Element(A + "buBlip") is not null ? new Marker(Mark.Bullet) : null;
    }

    /// <summary>Text runs and fields, a line break as '\n', bold, italic and links to outside the presentation.</summary>
    private static List<DocRun> Runs(XElement p, Dictionary<string, string> links)
    {
        var runs = new List<DocRun>();
        foreach (var e in p.Elements())
        {
            if (e.Name == A + "br")
            {
                runs.Add(new DocRun("\n", false, false, null));
            }
            else if (e.Name == A + "r" || e.Name == A + "fld")
            {
                var properties = e.Element(A + "rPr");
                var link = properties?.Element(A + "hlinkClick")?.Attribute(R + "id")?.Value is { } id && links.TryGetValue(id, out var target) ? target : null;
                runs.Add(new DocRun(e.Element(A + "t")?.Value ?? "", On(properties, "b"), On(properties, "i"), link));
            }
        }

        return runs;
    }

    private static bool HasText(List<DocRun> runs) => runs.Any(r => !string.IsNullOrWhiteSpace(r.Text));

    private static bool On(XElement? properties, string name) => (string?)properties?.Attribute(name) is "1" or "true";

    /// <summary>A table, a cell merged into the one before or above it empty.</summary>
    private static DocTable Table(XElement table, Dictionary<string, string> links)
    {
        var rows = new List<IReadOnlyList<IReadOnlyList<DocBlock>>>();
        foreach (var row in table.Elements(A + "tr"))
        {
            var cells = new List<IReadOnlyList<DocBlock>>();
            foreach (var cell in row.Elements(A + "tc"))
            {
                cells.Add(On(cell, "hMerge") || On(cell, "vMerge") ? [] : Paragraphs(cell.Element(A + "txBody"), [], links));
            }

            rows.Add(cells);
        }

        return new(rows);
    }

    /// <summary>The text of a SmartArt graphic, from its data: its nodes and assistants in the order they are saved.</summary>
    private List<DocBlock> Diagram(string part)
    {
        var blocks = new List<DocBlock>();
        foreach (var point in Part(part)?.Root?.Element(Dgm + "ptLst")?.Elements(Dgm + "pt") ?? [])
        {
            if ((string?)point.Attribute("type") is null or "node" or "asst")
            {
                blocks.AddRange(Paragraphs(point.Element(Dgm + "t"), [], []));
            }
        }

        return blocks;
    }

    private static string? Related(List<(string Id, string Type, string Target, bool External)> rels, string type) =>
        rels.FirstOrDefault(r => r.Type.EndsWith(type, StringComparison.Ordinal) && !r.External).Target;

    /// <summary>A part, loaded once: a deck's slides share a few layouts and a master.</summary>
    private XDocument? Part(string path)
    {
        if (!_parts.TryGetValue(path, out var document))
        {
            document = OoxmlPackage.Load(_zip, path);
            _parts[path] = document;
        }

        return document;
    }
}
