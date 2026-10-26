using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DropWheel.Engines.Document;

/// <summary>A paragraph or a table of a Word document, in body order.</summary>
internal abstract record DocBlock;

/// <param name="Heading">1 to 9 for a heading, 1 for the title too, 0 for body text.</param>
/// <param name="List">Where the paragraph sits in a list; null outside one, and for a heading.</param>
internal sealed record DocParagraph(int Heading, DocListItem? List, IReadOnlyList<DocRun> Runs) : DocBlock
{
    public string Text => string.Concat(Runs.Select(r => r.Text));
}

/// <param name="Number">The number Word shows for an ordered item; 0 for a bullet.</param>
internal sealed record DocListItem(int Level, bool Ordered, int Number);

/// <param name="Rows">Each row's cells, each cell's blocks. A merged cell is the first cell and empty ones after or below it.</param>
internal sealed record DocTable(IReadOnlyList<IReadOnlyList<IReadOnlyList<DocBlock>>> Rows) : DocBlock;

/// <param name="Text">May hold tabs and line breaks ('\n').</param>
/// <param name="Link">The address of an external link.</param>
internal sealed record DocRun(string Text, bool Bold, bool Italic, string? Link);

/// <summary>
/// The text of a DOCX and the structure Markdown and HTML can carry: headings (from the built-in heading and title
/// styles, styles based on them, or an outline level), lists with Word's numbers, bold and italic, external links
/// and tables. Text boxes follow the paragraph they are anchored to. Inserted text is kept and deleted text dropped,
/// as Word shows with all changes accepted. Images, fonts, sizes, colours, headers, footers, footnotes and
/// comments are not read. Hand-parsed, through <see cref="OoxmlPackage"/>.
/// </summary>
internal sealed partial class DocxReader
{
    private static readonly XNamespace W = DocxPackage.W;
    private static readonly XNamespace R = DocxPackage.R;
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    /// <summary>A style chain longer than this is taken for a loop.</summary>
    private const int MaxStyleDepth = 16;

    private sealed record Style(string? Name, string? BasedOn, int? OutlineLevel, (string NumId, int? Level)? Numbering, bool? Bold, bool? Italic);

    private sealed record ListLevel(bool Ordered, int Start);

    private sealed record ListDefinition(string AbstractId, IReadOnlyDictionary<int, ListLevel> Levels, IReadOnlyDictionary<int, int> StartOverrides);

    private readonly record struct Look(bool Bold, bool Italic);

    /// <summary>A complex field: its instruction until the separator, its shown result after it.</summary>
    private sealed class Field
    {
        public StringBuilder Instruction { get; } = new();

        public bool InResult { get; set; }

        public string? Link { get; set; }
    }

    private readonly Dictionary<string, Style> _styles;
    private readonly string? _defaultStyle;
    private readonly Dictionary<string, ListDefinition> _lists;
    private readonly Dictionary<string, string> _links;
    private readonly Dictionary<(string AbstractId, int Level), int> _counters = [];
    private readonly HashSet<(string NumId, int Level)> _started = [];
    private readonly Stack<Field> _fields = new();

    private DocxReader(XDocument? styles, XDocument? numbering, Dictionary<string, string> links)
    {
        (_styles, _defaultStyle) = ReadStyles(styles);
        _lists = ReadNumbering(numbering);
        _links = links;
    }

    /// <exception cref="InvalidDataException">The file is encrypted, an old .doc, damaged, or not a Word document; the message says which for the user.</exception>
    public static IReadOnlyList<DocBlock> Read(string path, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(path);
        using (var zip = OoxmlPackage.Open(path, "DOCX", ".doc"))
        {
            XDocument? document;
            DocxReader reader;
            try
            {
                var main = OoxmlPackage.Main(zip, "word/document.xml");
                document = OoxmlPackage.Load(zip, main);
                var rels = OoxmlPackage.Relationships(zip, main);
                XDocument? Part(string type) => rels.FirstOrDefault(r => r.Type.EndsWith(type, StringComparison.Ordinal) && !r.External) is { Target: { } target } ? OoxmlPackage.Load(zip, target) : null;
                var links = rels.Where(r => r.External).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Target);
                reader = new DocxReader(Part("/styles"), Part("/numbering"), links);
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException)
            {
                // A part that is not well-formed XML, or an entry the zip reader cannot inflate.
                throw new InvalidDataException($"{name} 无法读取：{ex.Message}", ex);
            }

            if (document?.Root?.Name != W + "document")
            {
                throw new InvalidDataException($"{name} 不是 Word 文档，或是 Strict Open XML 格式，暂不支持");
            }

            return document.Root.Element(W + "body") is { } body ? reader.Blocks(body, cancellationToken) : [];
        }
    }

    /// <summary>Whether any block has more than white space.</summary>
    public static bool HasText(IEnumerable<DocBlock> blocks) => blocks.Any(b => b switch
    {
        DocParagraph p => !string.IsNullOrWhiteSpace(p.Text),
        DocTable t => t.Rows.Any(row => row.Any(HasText)),
        _ => false,
    });

    private List<DocBlock> Blocks(XElement container, CancellationToken cancellationToken)
    {
        var blocks = new List<DocBlock>();
        foreach (var e in Content(container))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (e.Name == W + "p")
            {
                Paragraph(e, blocks, cancellationToken);
            }
            else if (e.Name == W + "tbl")
            {
                blocks.Add(Table(e, cancellationToken));
            }
        }

        return blocks;
    }

    /// <summary>
    /// The children of <paramref name="container"/> with the wrappers that only group or mark content opened up:
    /// content controls, custom XML, smart tags, inserted and moved-to text, and the first choice of alternate content.
    /// Deleted and moved-from text is not among them.
    /// </summary>
    private static IEnumerable<XElement> Content(XElement container)
    {
        foreach (var e in container.Elements())
        {
            var inner = e.Name == W + "sdt" ? e.Element(W + "sdtContent")
                : e.Name == Mc + "AlternateContent" ? Choice(e)
                : e.Name == W + "customXml" || e.Name == W + "smartTag" || e.Name == W + "ins" || e.Name == W + "moveTo" || e.Name == W + "dir" || e.Name == W + "bdo" ? e
                : null;
            if (inner is null)
            {
                yield return e;
                continue;
            }

            foreach (var child in Content(inner))
            {
                yield return child;
            }
        }
    }

    /// <summary>Word writes the same text box twice, as DrawingML in the choice and VML in the fallback; one is enough.</summary>
    private static XElement? Choice(XElement alternate) => alternate.Element(Mc + "Choice") ?? alternate.Element(Mc + "Fallback");

    private void Paragraph(XElement p, List<DocBlock> blocks, CancellationToken cancellationToken)
    {
        var properties = p.Element(W + "pPr");
        var chain = Chain(Val(properties?.Element(W + "pStyle")) ?? _defaultStyle).ToList();
        var heading = HeadingLevel(properties, chain);
        var list = heading > 0 ? null : ListItem(properties, chain);

        // A heading's own style is usually bold; Markdown and HTML make it stand out anyway.
        var look = heading > 0 ? default : new Look(First(chain, s => s.Bold) ?? false, First(chain, s => s.Italic) ?? false);
        var runs = new List<DocRun>();
        var boxes = new List<DocBlock>();
        Inline(p, look, null, runs, boxes, cancellationToken);
        blocks.Add(new DocParagraph(heading, list, Merge(runs)));
        blocks.AddRange(boxes);
    }

    private static T? First<T>(IEnumerable<Style> chain, Func<Style, T?> pick) where T : struct =>
        chain.Select(pick).FirstOrDefault(v => v.HasValue);

    private static int HeadingLevel(XElement? properties, List<Style> chain)
    {
        if (OutlineLevel(properties) is { } direct)
        {
            return direct;
        }

        foreach (var style in chain)
        {
            if (style.OutlineLevel is { } level)
            {
                return level;
            }

            if (style.Name is { } name && HeadingName().Match(name) is { Success: true } m)
            {
                return m.Groups[1].Success ? m.Groups[1].Value[0] - '0' : 1;
            }
        }

        return 0;
    }

    /// <summary>Built-in style names are stored in English whatever the language of Word.</summary>
    [GeneratedRegex(@"^(?:heading\s*([1-9])|title)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingName();

    /// <summary>1 to 9 for outline levels 0 to 8, 0 for level 9, body text; null when not set here.</summary>
    private static int? OutlineLevel(XElement? properties) =>
        IntVal(properties?.Element(W + "outlineLvl")) is { } level ? (level is >= 0 and < 9 ? level + 1 : 0) : null;

    private DocListItem? ListItem(XElement? properties, List<Style> chain)
    {
        var numbering = properties?.Element(W + "numPr");
        var numId = Val(numbering?.Element(W + "numId"));
        var ilvl = IntVal(numbering?.Element(W + "ilvl"));
        if (numId is null && chain.Select(s => s.Numbering).FirstOrDefault(n => n is not null) is { } styled)
        {
            (numId, ilvl) = (styled.NumId, ilvl ?? styled.Level);
        }

        var level = Math.Clamp(ilvl ?? 0, 0, 8);
        if (numId is null || !_lists.TryGetValue(numId, out var list) || !list.Levels.TryGetValue(level, out var format))
        {
            return null;
        }

        // Lists on one definition share their numbers, as in Word, unless one restarts them.
        var number = 0;
        if (format.Ordered)
        {
            var key = (list.AbstractId, level);
            number = _started.Add((numId, level)) && list.StartOverrides.TryGetValue(level, out var restart) ? restart
                : _counters.TryGetValue(key, out var previous) ? previous + 1
                : format.Start;
            _counters[key] = number;
        }

        foreach (var deeper in _counters.Keys.Where(k => k.AbstractId == list.AbstractId && k.Level > level).ToList())
        {
            _counters.Remove(deeper);
            _started.RemoveWhere(s => s.Level == deeper.Level && _lists.TryGetValue(s.NumId, out var other) && other.AbstractId == list.AbstractId);
        }

        return new(level, format.Ordered, number);
    }

    private void Inline(XElement parent, Look look, string? link, List<DocRun> runs, List<DocBlock> boxes, CancellationToken cancellationToken)
    {
        foreach (var e in Content(parent))
        {
            if (e.Name == W + "r")
            {
                Run(e, look, link, runs, boxes, cancellationToken);
            }
            else if (e.Name == W + "hyperlink")
            {
                var target = e.Attribute(R + "id")?.Value is { } id && _links.TryGetValue(id, out var address) ? address : null;
                Inline(e, look, target ?? link, runs, boxes, cancellationToken);
            }
            else if (e.Name == W + "fldSimple")
            {
                Inline(e, look, Hyperlink(e.Attribute(W + "instr")?.Value) ?? link, runs, boxes, cancellationToken);
            }
            else if ((e.Name == M + "oMath" || e.Name == M + "oMathPara") && !InInstruction)
            {
                runs.Add(new(string.Concat(e.Descendants(M + "t").Select(t => t.Value)), look.Bold, look.Italic, FieldLink ?? link));
            }
        }
    }

    private void Run(XElement r, Look paragraph, string? link, List<DocRun> runs, List<DocBlock> boxes, CancellationToken cancellationToken)
    {
        var properties = r.Element(W + "rPr");
        var chain = Chain(Val(properties?.Element(W + "rStyle"))).ToList();
        var look = new Look(
            Toggle(properties?.Element(W + "b")) ?? First(chain, s => s.Bold) ?? paragraph.Bold,
            Toggle(properties?.Element(W + "i")) ?? First(chain, s => s.Italic) ?? paragraph.Italic);
        var hidden = Toggle(properties?.Element(W + "vanish")) == true;
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length > 0 && !hidden && !InInstruction)
            {
                runs.Add(new(text.ToString(), look.Bold, look.Italic, FieldLink ?? link));
            }

            text.Clear();
        }

        foreach (var e in Content(r))
        {
            var name = e.Name;
            if (name == W + "fldChar")
            {
                Flush();
                FieldChar(e.Attribute(W + "fldCharType")?.Value);
            }
            else if (name == W + "instrText")
            {
                if (_fields.TryPeek(out var field) && !field.InResult)
                {
                    field.Instruction.Append(e.Value);
                }
            }
            else if (name == W + "t")
            {
                text.Append(e.Value);
            }
            else if (name == W + "tab" || name == W + "ptab")
            {
                text.Append('\t');
            }
            else if (name == W + "cr" || (name == W + "br" && e.Attribute(W + "type")?.Value is null or "textWrapping"))
            {
                text.Append('\n');
            }
            else if (name == W + "noBreakHyphen")
            {
                text.Append('-');
            }
            else if (name == W + "drawing" || name == W + "pict")
            {
                Flush();
                foreach (var box in TextBoxes(e))
                {
                    boxes.AddRange(Blocks(box, cancellationToken));
                }
            }
        }

        Flush();
    }

    private bool InInstruction => _fields.Any(f => !f.InResult);

    /// <summary>The innermost field's link, such as a HYPERLINK field Word wrote instead of a hyperlink element.</summary>
    private string? FieldLink => _fields.FirstOrDefault(f => f.Link is not null)?.Link;

    private void FieldChar(string? type)
    {
        switch (type)
        {
            case "begin":
                _fields.Push(new());
                break;
            case "separate" when _fields.TryPeek(out var field):
                field.InResult = true;
                field.Link = Hyperlink(field.Instruction.ToString());
                break;
            case "end":
                _fields.TryPop(out _);
                break;
        }
    }

    /// <summary>The address of a HYPERLINK field instruction, or null for another field or a link within the document.</summary>
    private static string? Hyperlink(string? instruction) =>
        instruction is not null && HyperlinkField().Match(instruction) is { Success: true } m ? m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value : null;

    [GeneratedRegex(@"^\s*HYPERLINK\s+(?:""([^""]+)""|([^\s\\""]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HyperlinkField();

    /// <summary>The outermost text box contents in a drawing or VML picture; text boxes in them are read with their paragraphs.</summary>
    private static IEnumerable<XElement> TextBoxes(XElement e)
    {
        foreach (var child in e.Elements())
        {
            var inner = child.Name == W + "txbxContent" ? null : child.Name == Mc + "AlternateContent" ? Choice(child) : child;
            if (inner is null)
            {
                yield return child;
                continue;
            }

            foreach (var box in TextBoxes(inner))
            {
                yield return box;
            }
        }
    }

    private DocTable Table(XElement table, CancellationToken cancellationToken)
    {
        var rows = new List<IReadOnlyList<IReadOnlyList<DocBlock>>>();
        foreach (var tr in Content(table).Where(e => e.Name == W + "tr"))
        {
            var cells = new List<IReadOnlyList<DocBlock>>();
            foreach (var tc in Content(tr).Where(e => e.Name == W + "tc"))
            {
                var properties = tc.Element(W + "tcPr");
                var merge = properties?.Element(W + "vMerge");
                cells.Add(merge is not null && (merge.Attribute(W + "val")?.Value ?? "continue") == "continue" ? [] : Blocks(tc, cancellationToken));
                for (var span = IntVal(properties?.Element(W + "gridSpan")) ?? 1; span > 1; span--)
                {
                    cells.Add([]);
                }
            }

            rows.Add(cells);
        }

        return new(rows);
    }

    /// <summary>Runs that look the same and go to the same place joined into one, empty ones dropped.</summary>
    private static List<DocRun> Merge(List<DocRun> runs)
    {
        var merged = new List<DocRun>();
        foreach (var run in runs.Where(r => r.Text.Length > 0))
        {
            if (merged is [.., var last] && last with { Text = run.Text } == run)
            {
                merged[^1] = last with { Text = last.Text + run.Text };
            }
            else
            {
                merged.Add(run);
            }
        }

        return merged;
    }

    /// <summary>The style and the ones it is based on, nearest first.</summary>
    private IEnumerable<Style> Chain(string? id)
    {
        for (var depth = 0; id is not null && depth < MaxStyleDepth && _styles.TryGetValue(id, out var style); depth++)
        {
            yield return style;
            id = style.BasedOn;
        }
    }

    private static (Dictionary<string, Style> Styles, string? Default) ReadStyles(XDocument? document)
    {
        var styles = new Dictionary<string, Style>(StringComparer.Ordinal);
        string? fallback = null;
        foreach (var style in document?.Root?.Elements(W + "style") ?? [])
        {
            if (style.Attribute(W + "styleId")?.Value is not { } id)
            {
                continue;
            }

            var paragraph = style.Element(W + "pPr");
            var run = style.Element(W + "rPr");
            var numbering = paragraph?.Element(W + "numPr");
            styles.TryAdd(id, new(
                Val(style.Element(W + "name")),
                Val(style.Element(W + "basedOn")),
                OutlineLevel(paragraph),
                Val(numbering?.Element(W + "numId")) is { } numId ? (numId, IntVal(numbering?.Element(W + "ilvl"))) : null,
                Toggle(run?.Element(W + "b")),
                Toggle(run?.Element(W + "i"))));
            if (style.Attribute(W + "type")?.Value == "paragraph" && On(style.Attribute(W + "default")?.Value))
            {
                fallback ??= id;
            }
        }

        return (styles, fallback);
    }

    private static Dictionary<string, ListDefinition> ReadNumbering(XDocument? document)
    {
        var lists = new Dictionary<string, ListDefinition>(StringComparer.Ordinal);
        var root = document?.Root;
        if (root is null)
        {
            return lists;
        }

        var definitions = new Dictionary<string, Dictionary<int, ListLevel>>(StringComparer.Ordinal);
        foreach (var definition in root.Elements(W + "abstractNum"))
        {
            if (definition.Attribute(W + "abstractNumId")?.Value is not { } id)
            {
                continue;
            }

            var levels = new Dictionary<int, ListLevel>();
            foreach (var level in definition.Elements(W + "lvl"))
            {
                // A level numbered "none" shows no marker, so its paragraphs read as plain text.
                var format = Val(level.Element(W + "numFmt")) ?? "decimal";
                if (IntVal(level, W + "ilvl") is { } ilvl && format != "none")
                {
                    levels.TryAdd(ilvl, new(format != "bullet", IntVal(level.Element(W + "start")) ?? 1));
                }
            }

            definitions.TryAdd(id, levels);
        }

        foreach (var num in root.Elements(W + "num"))
        {
            if (num.Attribute(W + "numId")?.Value is { } numId && Val(num.Element(W + "abstractNumId")) is { } abstractId
                && definitions.TryGetValue(abstractId, out var levels))
            {
                var restarts = new Dictionary<int, int>();
                foreach (var over in num.Elements(W + "lvlOverride"))
                {
                    if (IntVal(over, W + "ilvl") is { } ilvl && IntVal(over.Element(W + "startOverride")) is { } start)
                    {
                        restarts[ilvl] = start;
                    }
                }

                lists.TryAdd(numId, new(abstractId, levels, restarts));
            }
        }

        return lists;
    }

    private static string? Val(XElement? e) => e?.Attribute(W + "val")?.Value;

    private static int? IntVal(XElement? e) => IntVal(e, W + "val");

    private static int? IntVal(XElement? e, XName attribute) => int.TryParse(e?.Attribute(attribute)?.Value, out var value) ? value : null;

    /// <summary>On when the element is there without a value or with a true one; null when it is not there.</summary>
    private static bool? Toggle(XElement? e) => e is null ? null : e.Attribute(W + "val")?.Value is not { } value || On(value);

    private static bool On(string? value) => value is "1" or "true" or "on";
}
