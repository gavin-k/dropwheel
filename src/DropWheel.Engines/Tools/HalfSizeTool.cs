using DropWheel.Core.Conversion;
using DropWheel.Core.Wheel;
using ImageMagick;

namespace DropWheel.Engines.Tools;

/// <summary>
/// Halves the width and height, turned the right way up first; a recipe may ask for another percentage. The
/// copy is named with its new size.
/// </summary>
public sealed class HalfSizeTool(TimeProvider? time = null) : ImageTool(time)
{
    public const string PercentOption = "percent";
    private const int DefaultPercent = 50;

    public override string Id => "tool.half-size";

    public override string Label => L.T("缩小");

    public override string Hint => L.T("一半尺寸");

    public override string? Glyph => "\uE742"; // ResizeTouchSmaller

    protected override IReadOnlyCollection<string> Formats => Image.MagickImageConverter.Formats.Keys;

    protected override string Suffix => "-{w}x{h}";

    /// <summary>percent: the side length as a share of the source's, 50 by default.</summary>
    public override IReadOnlyList<OptionSpec> Options { get; } = [OptionSpec.Range(PercentOption, L.T("边长缩到原来的百分之几，默认 50"), 1, 100)];

    protected override Rendered? Render(string source, string formatId, ConversionRequest request)
    {
        var percent = RequestOptions.Number(request, PercentOption, DefaultPercent);
        using var frames = Frames(source);
        foreach (var image in frames)
        {
            image.AutoOrient();
            image.Resize(new MagickGeometry(Scale(image.Width, percent), Scale(image.Height, percent)) { IgnoreAspectRatio = true });
            image.ResetPage();
        }

        return new(frames.ToByteArray(MagickFormatOf(formatId)), (int)frames[0].Width, (int)frames[0].Height);
    }

    /// <summary>Rounded half up, so 50 of an odd size is what halving it was: (size + 1) / 2.</summary>
    private static uint Scale(uint size, int percent) => Math.Max(1, (uint)Math.Round(size * percent / 100.0, MidpointRounding.AwayFromZero));
}
