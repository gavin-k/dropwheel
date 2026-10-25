using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DropWheel.Engines.Subtitle;

/// <summary>
/// One timed line. <see cref="Text"/> separates lines with '\n' and keeps only the &lt;i&gt;, &lt;b&gt;
/// and &lt;u&gt; tags, the styling every supported format can carry.
/// </summary>
internal sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>Parsers and writers for SRT, WebVTT, ASS, SSA and LRC. Parsers skip what they do not understand rather than fail.</summary>
internal static partial class SubtitleFormats
{
    private const string Nl = "\r\n";

    /// <summary>LRC has no end times; the last line stays up this long.</summary>
    public static readonly TimeSpan LastLrcLine = TimeSpan.FromSeconds(5);

    private static readonly string[] DefaultAssEvents = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];

    public static IReadOnlyList<Cue> Parse(string formatId, string text) => formatId switch
    {
        "srt" => ParseSrtOrVtt(text, vtt: false),
        "vtt" => ParseSrtOrVtt(text, vtt: true),
        "ass" or "ssa" => ParseAss(text),
        "lrc" => ParseLrc(text),
        _ => throw new ArgumentOutOfRangeException(nameof(formatId), formatId, "not a subtitle format"),
    };

    public static string Write(string formatId, IReadOnlyList<Cue> cues)
    {
        var sorted = cues.OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
        return formatId switch
        {
            "srt" => WriteSrt(sorted),
            "vtt" => WriteVtt(sorted),
            "ass" => WriteAss(sorted, ssa: false),
            "ssa" => WriteAss(sorted, ssa: true),
            "lrc" => WriteLrc(sorted),
            _ => throw new ArgumentOutOfRangeException(nameof(formatId), formatId, "not a subtitle format"),
        };
    }

    // ---- SRT / WebVTT -------------------------------------------------------------------------------

    /// <summary>
    /// A cue is a timing line plus the lines up to the next blank one. Everything else (numbers, VTT
    /// identifiers, NOTE / STYLE / REGION blocks, the header) is ignored. Tolerates a missing blank line
    /// before the next cue's number.
    /// </summary>
    private static List<Cue> ParseSrtOrVtt(string text, bool vtt)
    {
        var cues = new List<Cue>();
        var lines = Lines(text);
        for (var i = 0; i < lines.Length; i++)
        {
            var timing = TimingRegex().Match(lines[i]);
            if (!timing.Success)
            {
                continue;
            }

            var body = new List<string>();
            var j = i + 1;
            for (; j < lines.Length && lines[j].Trim().Length > 0; j++)
            {
                if (TimingRegex().IsMatch(lines[j]))
                {
                    if (body.Count > 0 && body[^1].Trim().All(char.IsAsciiDigit))
                    {
                        body.RemoveAt(body.Count - 1);
                    }

                    break;
                }

                body.Add(lines[j]);
            }

            i = j - 1;
            var cueText = string.Join('\n', body);
            cueText = vtt ? CleanVtt(cueText) : CleanSrt(cueText);
            if (cueText.Length > 0)
            {
                cues.Add(new(TimeOf(timing.Groups["a"].Value), TimeOf(timing.Groups["b"].Value), cueText));
            }
        }

        return cues;
    }

    /// <summary>Drops &lt;font&gt; and the {\an8}-style ASS overrides some SRT files carry.</summary>
    private static string CleanSrt(string text) => Tidy(TagRegex().Replace(AssBlockRegex().Replace(text, ""), KeepBasicTag));

    /// <summary>Drops karaoke timestamps, classes, voices and ruby, then decodes entities.</summary>
    private static string CleanVtt(string text) => Tidy(WebUtility.HtmlDecode(TagRegex().Replace(TimestampTagRegex().Replace(text, ""), KeepBasicTag)));

    private static string KeepBasicTag(Match m)
    {
        var name = m.Groups["name"].Value.ToLowerInvariant();
        return name is "i" or "b" or "u" ? $"<{m.Groups["close"].Value}{name}>" : "";
    }

    private static string WriteSrt(List<Cue> cues)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            sb.Append(i + 1).Append(Nl)
                .Append(Clock(c.Start, ',')).Append(" --> ").Append(Clock(c.End, ',')).Append(Nl)
                .Append(c.Text.Replace("\n", Nl, StringComparison.Ordinal)).Append(Nl)
                .Append(Nl);
        }

        return sb.ToString();
    }

    private static string WriteVtt(List<Cue> cues)
    {
        var sb = new StringBuilder("WEBVTT").Append(Nl).Append(Nl);
        foreach (var c in cues)
        {
            sb.Append(Clock(c.Start, '.')).Append(" --> ").Append(Clock(c.End, '.')).Append(Nl)
                .Append(EscapeVtt(c.Text).Replace("\n", Nl, StringComparison.Ordinal)).Append(Nl)
                .Append(Nl);
        }

        return sb.ToString();
    }

    /// <summary>Escapes &amp;, &lt; and &gt; everywhere except in the basic tags, which also rules out "-->" in cue text.</summary>
    private static string EscapeVtt(string text)
    {
        var sb = new StringBuilder();
        var at = 0;
        foreach (Match tag in BasicTagRegex().Matches(text))
        {
            sb.Append(Escape(text[at..tag.Index])).Append(tag.Value);
            at = tag.Index + tag.Length;
        }

        return sb.Append(Escape(text[at..])).ToString();

        static string Escape(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static string Clock(TimeSpan t, char fraction) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{fraction}{t.Milliseconds:000}");

    // ---- ASS / SSA ----------------------------------------------------------------------------------

    /// <summary>Dialogue lines of the [Events] section, columns found through its Format line.</summary>
    private static List<Cue> ParseAss(string text)
    {
        var cues = new List<Cue>();
        string[] format = DefaultAssEvents;
        var inEvents = false;
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents)
            {
                continue;
            }

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                format = line["Format:".Length..].Split(',').Select(f => f.Trim()).ToArray();
                continue;
            }

            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fields = line["Dialogue:".Length..].TrimStart().Split(',', format.Length);
            var start = Array.FindIndex(format, f => f.Equals("Start", StringComparison.OrdinalIgnoreCase));
            var end = Array.FindIndex(format, f => f.Equals("End", StringComparison.OrdinalIgnoreCase));
            var body = Array.FindIndex(format, f => f.Equals("Text", StringComparison.OrdinalIgnoreCase));
            if (fields.Length < format.Length || start < 0 || end < 0 || body < 0
                || !TryTimeOf(fields[start], out var from) || !TryTimeOf(fields[end], out var to))
            {
                continue;
            }

            var cueText = CleanAss(fields[body]);
            if (cueText.Length > 0)
            {
                cues.Add(new(from, to, cueText));
            }
        }

        return cues;
    }

    /// <summary>
    /// Keeps \i, \b and \u from override blocks as tags, turns \N and \n into line breaks and \h into a
    /// space, and drops every other override plus the drawing commands between \p1 and \p0.
    /// </summary>
    private static string CleanAss(string text)
    {
        var sb = new StringBuilder();
        var drawing = false;
        var open = new HashSet<char>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{' && text.IndexOf('}', i) is var close and > 0)
            {
                foreach (Match m in AssToggleRegex().Matches(text[(i + 1)..close]))
                {
                    var tag = m.Groups["tag"].Value[0];
                    var on = m.Groups["v"].Value.Any(d => d != '0');
                    if (tag == 'p')
                    {
                        drawing = on;
                    }
                    else if (on ? open.Add(tag) : open.Remove(tag))
                    {
                        sb.Append(on ? $"<{tag}>" : $"</{tag}>");
                    }
                }

                i = close;
            }
            else if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is 'N' or 'n' or 'h')
            {
                sb.Append(text[i + 1] == 'h' ? ' ' : '\n');
                i++;
            }
            else if (!drawing)
            {
                sb.Append(text[i]);
            }
        }

        return Tidy(sb.ToString());
    }

    private static string WriteAss(List<Cue> cues, bool ssa)
    {
        var sb = new StringBuilder();
        sb.Append("[Script Info]").Append(Nl)
            .Append("; Converted by DropWheel").Append(Nl)
            .Append("ScriptType: ").Append(ssa ? "v4.00" : "v4.00+").Append(Nl)
            .Append("PlayResX: 384").Append(Nl)
            .Append("PlayResY: 288").Append(Nl);
        if (ssa)
        {
            sb.Append(Nl).Append("[V4 Styles]").Append(Nl)
                .Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, TertiaryColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, AlphaLevel, Encoding").Append(Nl)
                .Append("Style: Default,Microsoft YaHei,16,16777215,16777215,0,0,0,0,1,1,0,2,10,10,10,0,1").Append(Nl)
                .Append(Nl).Append("[Events]").Append(Nl)
                .Append("Format: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text").Append(Nl);
        }
        else
        {
            sb.Append("ScaledBorderAndShadow: yes").Append(Nl)
                .Append(Nl).Append("[V4+ Styles]").Append(Nl)
                .Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding").Append(Nl)
                .Append("Style: Default,Microsoft YaHei,16,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1").Append(Nl)
                .Append(Nl).Append("[Events]").Append(Nl)
                .Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text").Append(Nl);
        }

        foreach (var c in cues)
        {
            var body = BasicTagRegex().Replace(c.Text, m => m.Value[1] == '/' ? $"{{\\{m.Value[2]}0}}" : $"{{\\{m.Value[1]}1}}").Replace("\n", "\\N", StringComparison.Ordinal);
            sb.Append("Dialogue: ").Append(ssa ? "Marked=0" : "0").Append(',')
                .Append(Centis(c.Start, withHours: true)).Append(',').Append(Centis(c.End, withHours: true))
                .Append(",Default,,0,0,0,,").Append(body).Append(Nl);
        }

        return sb.ToString();
    }

    // ---- LRC ----------------------------------------------------------------------------------------

    /// <summary>
    /// "[mm:ss.xx]text" lines, several timestamps per line allowed, [offset:±ms] honoured, enhanced
    /// per-word times dropped. A cue ends where the next line starts; an empty line only marks that end.
    /// </summary>
    private static List<Cue> ParseLrc(string text)
    {
        var entries = new List<(TimeSpan At, string Text)>();
        var offset = TimeSpan.Zero;
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (OffsetRegex().Match(line) is { Success: true } o)
            {
                offset = TimeSpan.FromMilliseconds(int.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture));
                continue;
            }

            var times = new List<TimeSpan>();
            var at = 0;
            while (LrcTimeRegex().Match(line, at) is { Success: true } m)
            {
                times.Add(Time(m.Groups["h"].Value, m.Groups["m"].Value, m.Groups["s"].Value, m.Groups["f"].Value));
                at += m.Length;
            }

            var lyric = Tidy(WordTimeRegex().Replace(line[at..], ""));
            foreach (var t in times)
            {
                // A positive offset makes the lyrics appear earlier.
                entries.Add((t - offset < TimeSpan.Zero ? TimeSpan.Zero : t - offset, lyric));
            }
        }

        var ordered = entries.OrderBy(e => e.At).ToList();
        var cues = new List<Cue>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Text.Length == 0)
            {
                continue;
            }

            var next = ordered.Skip(i + 1).Select(e => e.At).FirstOrDefault(t => t > ordered[i].At);
            cues.Add(new(ordered[i].At, next > TimeSpan.Zero ? next : ordered[i].At + LastLrcLine, ordered[i].Text));
        }

        return cues;
    }

    /// <summary>One line per cue with the tags stripped; an empty timestamped line marks the end when a gap follows.</summary>
    private static string WriteLrc(List<Cue> cues)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            sb.Append('[').Append(Centis(c.Start, withHours: false)).Append(']')
                .Append(BasicTagRegex().Replace(c.Text, "").Replace('\n', ' ')).Append(Nl);
            if (i + 1 == cues.Count || cues[i + 1].Start - c.End >= TimeSpan.FromMilliseconds(10))
            {
                sb.Append('[').Append(Centis(c.End, withHours: false)).Append(']').Append(Nl);
            }
        }

        return sb.ToString();
    }

    /// <summary>Centisecond clock: "h:mm:ss.cc" for ASS, or "mm:ss.cc" with the minutes running past 59 for LRC.</summary>
    private static string Centis(TimeSpan t, bool withHours)
    {
        var cs = (long)Math.Round(t.TotalMilliseconds / 10, MidpointRounding.AwayFromZero);
        var (s, f) = (cs / 100 % 60, cs % 100);
        return withHours
            ? string.Create(CultureInfo.InvariantCulture, $"{cs / 360000}:{cs / 6000 % 60:00}:{s:00}.{f:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{cs / 6000:00}:{s:00}.{f:00}");
    }

    // ---- shared -------------------------------------------------------------------------------------

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    /// <summary>Trims lines, drops empty ones, and balances the basic tags so every writer gets well-formed text.</summary>
    private static string Tidy(string text)
    {
        var joined = string.Join('\n', text.Replace('\u00A0', ' ').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        var sb = new StringBuilder();
        var open = new List<char>();
        var at = 0;
        foreach (Match tag in BasicTagRegex().Matches(joined))
        {
            sb.Append(joined, at, tag.Index - at);
            at = tag.Index + tag.Length;
            var closing = tag.Value[1] == '/';
            var name = tag.Value[closing ? 2 : 1];
            if (!closing && !open.Contains(name))
            {
                open.Add(name);
                sb.Append(tag.Value);
            }
            else if (closing && open.Remove(name))
            {
                sb.Append(tag.Value);
            }
        }

        sb.Append(joined, at, joined.Length - at);
        for (var i = open.Count - 1; i >= 0; i--)
        {
            sb.Append("</").Append(open[i]).Append('>');
        }

        return BasicTagRegex().Replace(sb.ToString(), "").Trim().Length == 0 ? "" : sb.ToString().Trim();
    }

    private static TimeSpan TimeOf(string clock) => TryTimeOf(clock, out var t) ? t : throw new FormatException($"not a time: {clock}");

    private static bool TryTimeOf(string clock, out TimeSpan time)
    {
        var m = ClockRegex().Match(clock.Trim());
        time = m.Success ? Time(m.Groups["h"].Value, m.Groups["m"].Value, m.Groups["s"].Value, m.Groups["f"].Value) : default;
        return m.Success;
    }

    /// <summary>Fractions are read as decimals: ".5", ".50" and ".500" are all half a second.</summary>
    private static TimeSpan Time(string h, string m, string s, string f) =>
        new TimeSpan(0, h.Length > 0 ? int.Parse(h, CultureInfo.InvariantCulture) : 0, int.Parse(m, CultureInfo.InvariantCulture), int.Parse(s, CultureInfo.InvariantCulture))
        + TimeSpan.FromMilliseconds(f.Length > 0 ? int.Parse(f.PadRight(3, '0'), CultureInfo.InvariantCulture) : 0);

    private const string ClockPattern = @"(?:\d+:)?\d{1,2}:\d{1,2}(?:[,.]\d{1,3})?";

    [GeneratedRegex(@"^(?:(?<h>\d+):)?(?<m>\d{1,2}):(?<s>\d{1,2})(?:[,.](?<f>\d{1,3}))?$")]
    private static partial Regex ClockRegex();

    [GeneratedRegex(@"^\s*(?<a>" + ClockPattern + @")\s*-->\s*(?<b>" + ClockPattern + ")")]
    private static partial Regex TimingRegex();

    [GeneratedRegex(@"<(?<close>/?)(?<name>[A-Za-z]+)(?:[.\s][^>]*)?>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("</?[ibu]>")]
    private static partial Regex BasicTagRegex();

    [GeneratedRegex(@"<\d[^>]*>")]
    private static partial Regex TimestampTagRegex();

    [GeneratedRegex(@"\{\\[^}]*\}")]
    private static partial Regex AssBlockRegex();

    [GeneratedRegex(@"\\(?<tag>[ibup])(?<v>\d+)")]
    private static partial Regex AssToggleRegex();

    [GeneratedRegex(@"\G\[(?<h>)(?<m>\d+):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?\]")]
    private static partial Regex LrcTimeRegex();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"<\d+:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordTimeRegex();
}
