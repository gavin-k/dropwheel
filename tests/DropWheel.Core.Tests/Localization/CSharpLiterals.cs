using System.Globalization;
using System.Text;

namespace DropWheel.Core.Tests.Localization;

/// <summary>A string literal in C# source: where it is, whether it is a plain "…" literal, and what it says.</summary>
internal sealed record Literal(int Start, int End, bool Plain, string Text);

/// <summary>
/// Just enough of a C# lexer to find the string literals of a file and blank out its comments and literals, so the
/// catalog tests see code as code. It follows interpolation holes in "…" and @"…" literals; the holes of a raw
/// literal stay part of its text.
/// </summary>
internal static class CSharpLiterals
{
    public static (IReadOnlyList<Literal> Literals, string Code) Read(string source)
    {
        var code = source.ToCharArray();
        var literals = new List<Literal>();
        var i = 0;
        Code(source, ref i, code, literals, hole: false);
        return (literals, new string(code));
    }

    private static void Code(string s, ref int i, char[] code, List<Literal> literals, bool hole)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '/' && At(s, i + 1) == '/')
            {
                var end = s.IndexOf('\n', i);
                i = Blank(code, i, end < 0 ? s.Length : end);
            }
            else if (c == '/' && At(s, i + 1) == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = Blank(code, i, end < 0 ? s.Length : end + 2);
            }
            else if (c == '\'')
            {
                var j = i + 1;
                while (j < s.Length && s[j] != '\'')
                {
                    j += s[j] == '\\' ? 2 : 1;
                }

                i = Blank(code, i, Math.Min(j + 1, s.Length));
            }
            else if (StringStart(s, i) is { } start)
            {
                i = String(s, i, start.Dollars, start.Verbatim, start.Quotes, code, literals);
            }
            else if (hole && c == '{')
            {
                depth++;
                i++;
            }
            else if (hole && c == '}')
            {
                if (depth == 0)
                {
                    return;
                }

                depth--;
                i++;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>The prefix of a string literal starting at <paramref name="i"/>, if one does.</summary>
    private static (int Dollars, bool Verbatim, int Quotes)? StringStart(string s, int i)
    {
        if (i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_') && s[i] != '"')
        {
            return null;
        }

        var j = i;
        var dollars = 0;
        var verbatim = false;
        while (j < s.Length && (s[j] == '$' || s[j] == '@'))
        {
            if (s[j] == '$')
            {
                dollars++;
            }
            else
            {
                verbatim = true;
            }

            j++;
        }

        if (At(s, j) != '"')
        {
            return null;
        }

        var quotes = 0;
        while (At(s, j + quotes) == '"')
        {
            quotes++;
        }

        // "" is an empty literal, not the start of a raw one.
        return (dollars, verbatim, quotes >= 3 ? quotes : 1);
    }

    private static int String(string s, int i, int dollars, bool verbatim, int quotes, char[] code, List<Literal> literals)
    {
        var open = s.IndexOf('"', i);
        var start = i;
        var text = new StringBuilder();
        var j = open + quotes;
        if (quotes >= 3)
        {
            var close = s.IndexOf(new string('"', quotes), j, StringComparison.Ordinal);
            close = close < 0 ? s.Length : close;
            literals.Add(new Literal(start, close + quotes, false, s[j..close]));
            return Blank(code, start, Math.Min(close + quotes, s.Length));
        }

        while (j < s.Length)
        {
            var c = s[j];
            if (verbatim && c == '"' && At(s, j + 1) == '"')
            {
                text.Append('"');
                j += 2;
            }
            else if (c == '"')
            {
                j++;
                break;
            }
            else if (!verbatim && c == '\\')
            {
                j = Escape(s, j, text);
            }
            else if (dollars > 0 && c == '{' && At(s, j + 1) == '{')
            {
                text.Append('{');
                j += 2;
            }
            else if (dollars > 0 && c == '{')
            {
                Blank(code, start, j + 1);
                j++;
                Code(s, ref j, code, literals, hole: true);
                start = j;
                j++;
            }
            else
            {
                text.Append(c);
                j++;
            }
        }

        literals.Add(new Literal(i, j, dollars == 0 && !verbatim, text.ToString()));
        return Blank(code, start, j);
    }

    private static int Escape(string s, int j, StringBuilder text)
    {
        var next = At(s, j + 1);
        switch (next)
        {
            case 'n': text.Append('\n'); return j + 2;
            case 'r': text.Append('\r'); return j + 2;
            case 't': text.Append('\t'); return j + 2;
            case '0': text.Append('\0'); return j + 2;
            case 'u':
                text.Append((char)int.Parse(s.AsSpan(j + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                return j + 6;
            default: text.Append(next); return j + 2;
        }
    }

    private static int Blank(char[] code, int from, int to)
    {
        for (var k = from; k < to && k < code.Length; k++)
        {
            if (code[k] is not ('\n' or '\r'))
            {
                code[k] = ' ';
            }
        }

        return to;
    }

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';
}
