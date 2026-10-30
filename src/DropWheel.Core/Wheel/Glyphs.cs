using System.Globalization;
using DropWheel.Core.Conversion;

namespace DropWheel.Core.Wheel;

/// <summary>
/// The icons sectors draw above their label: Segoe Fluent Icons code points, one character each. Built-in items
/// name theirs through <see cref="IWheelOperation.Glyph"/>; a Convert sector takes the kind of file it makes; a
/// user tool's manifest names one in whatever spelling the icon list uses, which <see cref="Parse"/> accepts.
/// </summary>
public static class Glyphs
{
    /// <summary>More: a sector that opens as a ring.</summary>
    public const string Group = "";

    public static string ForKind(MediaKind kind) => kind switch
    {
        MediaKind.Image => "",   // Picture
        MediaKind.Video => "",   // Video
        MediaKind.Audio => "",   // Audio
        MediaKind.Pdf => "",     // PDF
        MediaKind.Slides => "",  // Tiles
        MediaKind.Sheet => "",   // GridView
        MediaKind.Subtitle => "", // Read
        MediaKind.Archive => "", // Package
        _ => "",                 // Document
    };

    /// <summary>
    /// "E7A7", "e7a7", "U+E7A7", "" (the six characters) or "&amp;#xE7A7;", or the character itself: the glyph as
    /// a one-character string, or null when the text is none of those or the code is outside the icon font's
    /// private-use range (E700 to F8FF).
    /// </summary>
    public static string? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (text.Length == 1)
        {
            return IsGlyph(text) ? text : null;
        }

        var hex = text switch
        {
            _ when text.StartsWith("U+", StringComparison.OrdinalIgnoreCase) => text[2..],
            _ when text.StartsWith("\\u", StringComparison.OrdinalIgnoreCase) => text[2..],
            _ when text.StartsWith("&#x", StringComparison.OrdinalIgnoreCase) && text.EndsWith(';') => text[3..^1],
            _ => text,
        };
        if (hex.Length == 4 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            var glyph = ((char)code).ToString();
            return IsGlyph(glyph) ? glyph : null;
        }

        return null;
    }

    /// <summary>One character in the icon font's private-use range.</summary>
    public static bool IsGlyph(string? text) => text is { Length: 1 } && text[0] >= '' && text[0] <= '';
}
