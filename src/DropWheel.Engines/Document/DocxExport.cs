using System.Globalization;
using System.Text;

namespace DropWheel.Engines.Document;

/// <summary>What <see cref="DocxReader"/> read, written out as plain text, Markdown or HTML.</summary>
internal static class DocxExport
{
    /// <summary>What a link may point at in the output; a javascript: or file: link keeps only its text.</summary>
    private static readonly string[] SafeSchemes = ["http", "https", "mailto", "ftp"];

    /// <summary>
    /// A line a paragraph, CRLF, as Word's own plain text save writes it: a list item with its bullet or number,
    /// two spaces a level; a table row a line, its cells between tabs.
    /// </summary>
    public static string Text(IReadOnlyList<DocBlock> blocks)
    {
        var lines = new List<string>();
        foreach (var block in blocks)
        {
            if (block is DocParagraph p)
            {
                var prefix = p.List is { } item ? new string(' ', 2 * item.Level) + (item.Ordered ? Number(item) : "• ") : "";
                lines.AddRange((prefix + p.Text).Split('\n'));
            }
            else if (block is DocTable t)
            {
                lines.AddRange(t.Rows.Select(row => string.Join('\t', row.Select(cell => string.Join(' ', Paragraphs(cell).Select(Flat))))));
            }
        }

        var first = lines.FindIndex(l => !string.IsNullOrWhiteSpace(l));
        var last = lines.FindLastIndex(l => !string.IsNullOrWhiteSpace(l));
        return first < 0 ? "" : string.Join("\r\n", lines[first..(last + 1)]) + "\r\n";
    }

    /// <summary>
    /// CommonMark with GitHub's pipe tables, LF: ATX headings, "-" and "1." lists four spaces a level, bold and
    /// italic with asterisks, links, a line break as a backslash. A table's first row is its header and a cell's
    /// paragraphs are joined with &lt;br&gt;. Empty paragraphs are left out.
    /// </summary>
    public static string Markdown(IReadOnlyList<DocBlock> blocks)
    {
        var md = new StringBuilder();

        // Whether each list open at the last item is ordered, outermost first.
        var kinds = new List<bool>();
        var wasItem = false;
        foreach (var block in blocks)
        {
            string chunk;
            var isItem = false;
            var apart = false;
            switch (block)
            {
                case DocParagraph { Heading: > 0 } p:
                    chunk = MarkdownLine([.. p.Runs.Select(r => r with { Text = r.Text.Replace('\n', ' ') })]);
                    if (chunk.EndsWith('#'))
                    {
                        // Or it would be taken for the optional closing sequence.
                        chunk = chunk[..^1] + "\\#";
                    }

                    chunk = chunk.Length > 0 ? new string('#', Math.Min(p.Heading, 6)) + " " + chunk : "";
                    break;
                case DocParagraph { List: { } item } p:
                    if (!wasItem)
                    {
                        kinds.Clear();
                    }

                    // One level deeper than the item before at most, or the indent would make a code block.
                    var level = Math.Min(item.Level, kinds.Count);

                    // Right after an item, a list of the other kind, or one counting from other than 1, needs a
                    // blank line before it, or Markdown reads its first line as more of the item before.
                    var opens = level == kinds.Count || kinds[level] != item.Ordered;
                    apart = opens && (level < kinds.Count || (item.Ordered && item.Number != 1));
                    kinds.RemoveRange(level, kinds.Count - level);
                    kinds.Add(item.Ordered);
                    isItem = true;
                    chunk = new string(' ', 4 * level) + (item.Ordered ? Number(item) : "- ") + MarkdownLines(p.Runs);
                    break;
                case DocParagraph p:
                    chunk = MarkdownLines(p.Runs);
                    break;
                case DocTable t:
                    chunk = MarkdownTable(t);
                    break;
                default:
                    continue;
            }

            if (chunk.Length == 0)
            {
                continue;
            }

            if (md.Length > 0)
            {
                md.Append(wasItem && isItem && !apart ? "\n" : "\n\n");
            }

            md.Append(chunk);
            wasItem = isItem;
        }

        return md.Append('\n').ToString();
    }

    /// <summary>
    /// A whole HTML page with a little CSS for reading: h1 to h6, paragraphs, nested ul and ol with Word's first
    /// number, tables, strong, em, links and line breaks.
    /// </summary>
    /// <param name="title">The page title, usually the file name without its extension.</param>
    public static string Html(IReadOnlyList<DocBlock> blocks, string title)
    {
        var html = new StringBuilder("""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">

            """);
        html.Append("<title>").Append(Encode(title)).Append("</title>\n");
        html.Append("""
            <style>
            body { font-family: system-ui, "Segoe UI", "Microsoft YaHei", sans-serif; line-height: 1.6; max-width: 48em; margin: 2em auto; padding: 0 1em; }
            table { border-collapse: collapse; margin: 1em 0; }
            td { border: 1px solid #999; padding: 0.3em 0.6em; vertical-align: top; }
            td p { margin: 0.2em 0; }
            </style>
            </head>
            <body>

            """);
        HtmlBlocks(html, blocks);
        return html.Append("</body>\n</html>\n").ToString();
    }

    private static string Number(DocListItem item) => item.Number.ToString(CultureInfo.InvariantCulture) + ". ";

    /// <summary>The paragraphs of a cell, those in tables inside it too, in order.</summary>
    private static IEnumerable<DocParagraph> Paragraphs(IEnumerable<DocBlock> blocks) => blocks.SelectMany(b => b switch
    {
        DocParagraph p => [p],
        DocTable t => t.Rows.SelectMany(row => row.SelectMany(Paragraphs)),
        _ => Enumerable.Empty<DocParagraph>(),
    });

    /// <summary>A paragraph's text on one line with single spaces, for a table cell in plain text.</summary>
    private static string Flat(DocParagraph p) => string.Join(' ', p.Text.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The runs between line breaks, a list for each line.</summary>
    private static List<List<DocRun>> Lines(IReadOnlyList<DocRun> runs)
    {
        var lines = new List<List<DocRun>> { new() };
        foreach (var run in runs)
        {
            var parts = run.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    lines.Add([]);
                }

                if (parts[i].Length > 0)
                {
                    lines[^1].Add(run with { Text = parts[i] });
                }
            }
        }

        return lines;
    }

    /// <summary>The lines of a paragraph joined with hard breaks, without the breaks at either end.</summary>
    private static string MarkdownLines(IReadOnlyList<DocRun> runs)
    {
        var lines = Lines(runs).Select(MarkdownLine).ToList();
        var first = lines.FindIndex(l => l.Length > 0);
        return first < 0 ? "" : string.Join("\\\n", lines[first..(lines.FindLastIndex(l => l.Length > 0) + 1)]);
    }

    /// <summary>One line of Markdown, trimmed, with what would start a heading, a list or a rule at its start escaped.</summary>
    private static string MarkdownLine(List<DocRun> runs)
    {
        var line = MarkdownInline(Trim(runs));
        if (line.Length > 0 && line[0] is '#' or '-' or '+' or '=')
        {
            return "\\" + line;
        }

        var digits = line.TakeWhile(char.IsAsciiDigit).Count();
        return digits > 0 && digits < line.Length && line[digits] is '.' or ')' ? line[..digits] + "\\" + line[digits..] : line;
    }

    /// <summary>Links around runs that go to the same place, emphasis inside.</summary>
    private static string MarkdownInline(List<DocRun> runs)
    {
        var md = new StringBuilder();
        foreach (var group in Groups(runs, r => r.Link))
        {
            if (Safe(group[0].Link) is { } link)
            {
                md.Append('[').Append(Emphasis(group, bold: true)).Append("](").Append(Destination(link)).Append(')');
            }
            else
            {
                md.Append(Emphasis(group, bold: true));
            }
        }

        return md.ToString();
    }

    /// <summary>
    /// Bold runs in **, italic ones inside in *. White space and punctuation at either end go outside the markers:
    /// CommonMark does not take ** for emphasis when punctuation inside meets a letter or a CJK character outside.
    /// </summary>
    private static string Emphasis(List<DocRun> runs, bool bold)
    {
        var md = new StringBuilder();
        foreach (var group in Groups(runs, r => bold ? r.Bold : r.Italic))
        {
            var on = bold ? group[0].Bold : group[0].Italic;
            var text = string.Concat(group.Select(r => r.Text));
            var start = text.TakeWhile(Outside).Count();
            if (!on || start == text.Length)
            {
                md.Append(bold ? Emphasis(group, bold: false) : Escape(text));
                continue;
            }

            var end = text.Length - text.Reverse().TakeWhile(Outside).Count();
            var marker = bold ? "**" : "*";
            var inner = Slice(group, start, end);
            md.Append(Escape(text[..start])).Append(marker).Append(bold ? Emphasis(inner, bold: false) : Escape(text[start..end])).Append(marker).Append(Escape(text[end..]));
        }

        return md.ToString();
    }

    private static bool Outside(char c) => char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);

    /// <summary>The runs cut to the characters from <paramref name="start"/> to <paramref name="end"/> of their joined text.</summary>
    private static List<DocRun> Slice(List<DocRun> runs, int start, int end)
    {
        var sliced = new List<DocRun>();
        var at = 0;
        foreach (var run in runs)
        {
            var from = Math.Max(start - at, 0);
            var to = Math.Min(end - at, run.Text.Length);
            if (from < to)
            {
                sliced.Add(run with { Text = run.Text[from..to] });
            }

            at += run.Text.Length;
        }

        return sliced;
    }

    /// <summary>The runs without the white space before the first character and after the last.</summary>
    private static List<DocRun> Trim(List<DocRun> runs)
    {
        var text = string.Concat(runs.Select(r => r.Text));
        var start = text.Length - text.TrimStart().Length;
        return Slice(runs, start, text.TrimEnd().Length);
    }

    private static string Escape(string text)
    {
        var md = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '~' or '|')
            {
                md.Append('\\');
            }

            md.Append(c == '\t' ? ' ' : c);
        }

        return md.ToString();
    }

    /// <summary>A link destination with what would end it or break it out percent-encoded.</summary>
    private static string Destination(string link) =>
        link.Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29").Replace("<", "%3C").Replace(">", "%3E");

    private static string MarkdownTable(DocTable table)
    {
        var width = table.Rows.Count > 0 ? table.Rows.Max(row => row.Count) : 0;
        if (width == 0)
        {
            return "";
        }

        string Row(IReadOnlyList<IReadOnlyList<DocBlock>> row) =>
            "| " + string.Join(" | ", Enumerable.Range(0, width).Select(i => i < row.Count ? MarkdownCell(row[i]) : "")) + " |";

        var md = new StringBuilder(Row(table.Rows[0])).Append('\n');
        md.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", width))).Append(" |");
        foreach (var row in table.Rows.Skip(1))
        {
            md.Append('\n').Append(Row(row));
        }

        return md.ToString();
    }

    /// <summary>A cell on one line: its paragraphs and line breaks as &lt;br&gt;, a list item with its bullet or number.</summary>
    private static string MarkdownCell(IReadOnlyList<DocBlock> cell) => string.Join("<br>", Paragraphs(cell)
        .Select(p => (p.List is { } item ? (item.Ordered ? Number(item) : "• ") : "") + string.Join("<br>", Lines(p.Runs).Select(l => MarkdownInline(Trim(l)))))
        .Where(text => text.Length > 0));

    private static void HtmlBlocks(StringBuilder html, IReadOnlyList<DocBlock> blocks)
    {
        // Whether each open list is ordered, outermost at the bottom; the last item of the innermost is open too.
        var lists = new Stack<bool>();
        void Close()
        {
            html.Append("</li>\n").Append(lists.Pop() ? "</ol>\n" : "</ul>\n");
        }

        foreach (var block in blocks)
        {
            if (block is DocParagraph { List: { } item } listed)
            {
                var level = Math.Min(item.Level, lists.Count);
                while (lists.Count > level + 1 || (lists.Count == level + 1 && lists.Peek() != item.Ordered))
                {
                    Close();
                }

                if (lists.Count == level + 1)
                {
                    html.Append("</li>\n");
                }
                else
                {
                    html.Append(!item.Ordered ? "<ul>\n" : item.Number == 1 ? "<ol>\n" : $"<ol start=\"{item.Number.ToString(CultureInfo.InvariantCulture)}\">\n");
                    lists.Push(item.Ordered);
                }

                html.Append("<li>");
                HtmlInline(html, listed.Runs);
                continue;
            }

            while (lists.Count > 0)
            {
                Close();
            }

            if (block is DocParagraph p && !string.IsNullOrWhiteSpace(p.Text))
            {
                var tag = p.Heading > 0 ? "h" + Math.Min(p.Heading, 6).ToString(CultureInfo.InvariantCulture) : "p";
                html.Append('<').Append(tag).Append('>');
                HtmlInline(html, p.Runs);
                html.Append("</").Append(tag).Append(">\n");
            }
            else if (block is DocTable t)
            {
                html.Append("<table>\n");
                foreach (var row in t.Rows)
                {
                    html.Append("<tr>\n");
                    foreach (var cell in row)
                    {
                        html.Append("<td>\n");
                        HtmlBlocks(html, cell);
                        html.Append("</td>\n");
                    }

                    html.Append("</tr>\n");
                }

                html.Append("</table>\n");
            }
        }

        while (lists.Count > 0)
        {
            Close();
        }
    }

    private static void HtmlInline(StringBuilder html, IReadOnlyList<DocRun> runs)
    {
        foreach (var group in Groups(runs, r => r.Link))
        {
            var link = Safe(group[0].Link);
            if (link is not null)
            {
                html.Append("<a href=\"").Append(Encode(link)).Append("\">");
            }

            foreach (var run in group)
            {
                html.Append(run.Bold ? "<strong>" : "").Append(run.Italic ? "<em>" : "");
                html.Append(Encode(run.Text).Replace("\n", "<br>\n"));
                html.Append(run.Italic ? "</em>" : "").Append(run.Bold ? "</strong>" : "");
            }

            if (link is not null)
            {
                html.Append("</a>");
            }
        }
    }

    private static string Encode(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>The link when it is an absolute address on one of <see cref="SafeSchemes"/>; null otherwise.</summary>
    private static string? Safe(string? link) =>
        link is not null && Uri.TryCreate(link, UriKind.Absolute, out var uri) && SafeSchemes.Contains(uri.Scheme) ? link : null;

    /// <summary>Neighbouring runs that agree on <paramref name="key"/>, a list for each stretch.</summary>
    private static List<List<DocRun>> Groups<T>(IEnumerable<DocRun> runs, Func<DocRun, T> key)
    {
        var groups = new List<List<DocRun>>();
        foreach (var run in runs)
        {
            if (groups is [.., var last] && EqualityComparer<T>.Default.Equals(key(last[0]), key(run)))
            {
                last.Add(run);
            }
            else
            {
                groups.Add([run]);
            }
        }

        return groups;
    }
}
