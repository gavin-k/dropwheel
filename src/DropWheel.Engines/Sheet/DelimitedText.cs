using System.Text;

namespace DropWheel.Engines.Sheet;

/// <summary>
/// CSV and TSV as RFC 4180 has them: a field with the delimiter, a quote or a line break is quoted and its quotes
/// doubled. Excel quotes TSV the same way, so both are read and written alike, CRLF between rows.
/// </summary>
internal static class DelimitedText
{
    /// <summary>
    /// The rows of a ".csv" or ".tsv". A CSV is split on commas, or on semicolons when its first line has more of
    /// those, as CSVs saved where the decimal mark is a comma are; a first line such as "sep=;", which Excel
    /// understands, names the delimiter and is not a row. A quote inside an unquoted field is kept as it is.
    /// </summary>
    public static List<string[]> Parse(string text, string format)
    {
        var start = 0;
        char delimiter;
        if (format == "tsv")
        {
            delimiter = '\t';
        }
        else if (text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && text.Length > 4 && (text.Length == 5 || text[5] is '\r' or '\n'))
        {
            delimiter = text[4];
            start = text.Length > 5 && text[5] == '\r' && text.Length > 6 && text[6] == '\n' ? 7 : 6;
        }
        else
        {
            delimiter = Guess(text);
        }

        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var quoted = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }
            }
            else if (c == '"' && field.Length == 0 && !quoted)
            {
                inQuotes = quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                quoted = false;
            }
            else if (c is '\r' or '\n')
            {
                row.Add(field.ToString());
                rows.Add([.. row]);
                row.Clear();
                field.Clear();
                quoted = false;
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0 || quoted)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }

        return rows;
    }

    /// <param name="width">Fields a row is padded to with empty ones, as Excel writes a sheet's used range; 0 for none.</param>
    public static void Write(TextWriter writer, IEnumerable<IReadOnlyList<string>> rows, char delimiter, int width = 0)
    {
        char[] special = [delimiter, '"', '\r', '\n'];
        foreach (var row in rows)
        {
            var count = Math.Max(row.Count, width);
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    writer.Write(delimiter);
                }

                if (i < row.Count)
                {
                    var value = row[i];
                    writer.Write(value.IndexOfAny(special) < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
                }
            }

            writer.Write("\r\n");
        }
    }

    /// <summary>A semicolon when the first line has more of them than commas outside quotes, otherwise a comma.</summary>
    private static char Guess(string text)
    {
        var (commas, semicolons, inQuotes) = (0, 0, false);
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && c is '\r' or '\n')
            {
                break;
            }
            else if (!inQuotes)
            {
                commas += c == ',' ? 1 : 0;
                semicolons += c == ';' ? 1 : 0;
            }
        }

        return semicolons > commas ? ';' : ',';
    }
}
