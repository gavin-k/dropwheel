using System.Text.RegularExpressions;

namespace DropWheel.Engines.Image;

/// <summary>
/// Refuses an SVG that points outside itself before a renderer reads it: an image, a stylesheet, a
/// <c>&lt;use&gt;</c> or an entity can name a local file or a web address, and rendering it would read the
/// file or go online. Only references into the SVG itself (<c>#id</c>) and inline <c>data:</c> are left;
/// a link on <c>&lt;a&gt;</c> is fine, nothing follows it while drawing.
/// </summary>
internal static partial class SvgGuard
{
    /// <summary>What the SVG points at outside itself, or null when it points nowhere.</summary>
    public static string? FindExternal(string svg)
    {
        foreach (var rule in new[] { Href(), Url(), Import(), Entity() })
        {
            if (rule.Match(svg) is { Success: true } found)
            {
                return found.Groups["what"].Value.Trim();
            }
        }

        return null;
    }

    [GeneratedRegex("""<(?!a[\s>/])[\w:-]+[^>]*?\bhref\s*=\s*(?<q>["'])(?>\s*)(?<what>(?!#|data:)[^"']*)\k<q>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Href();

    [GeneratedRegex("""url\((?>\s*["']?\s*)(?<what>(?!#|data:)[^)"']*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex("""(?<what>@import\b[^;]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Import();

    [GeneratedRegex("""(?<what><!ENTITY\b[^>]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Entity();
}
