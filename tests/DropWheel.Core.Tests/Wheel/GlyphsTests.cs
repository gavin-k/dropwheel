using DropWheel.Core.Conversion;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests.Wheel;

public sealed class GlyphsTests
{
    [Theory]
    [InlineData("E7A7")]
    [InlineData("e7a7")]
    [InlineData("U+E7A7")]
    [InlineData("\\uE7A7")]
    [InlineData("&#xE7A7;")]
    [InlineData("")]
    [InlineData("  E7A7 ")]
    public void A_code_point_in_any_common_spelling_parses_to_the_character(string text)
        => Glyphs.Parse(text).Should().Be("");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rotate")]
    [InlineData("0041")]
    [InlineData("A")]
    [InlineData("E7A7E7A7")]
    [InlineData("icon.svg")]
    public void Anything_else_is_no_glyph(string? text) => Glyphs.Parse(text).Should().BeNull();

    [Fact]
    public void Every_kind_of_file_has_a_glyph_in_the_icon_font()
    {
        foreach (var kind in Enum.GetValues<MediaKind>())
        {
            Glyphs.IsGlyph(Glyphs.ForKind(kind)).Should().BeTrue(kind.ToString());
        }

        Glyphs.IsGlyph(Glyphs.Group).Should().BeTrue();
    }
}
