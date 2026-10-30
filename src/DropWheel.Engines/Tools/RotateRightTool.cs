using DropWheel.Core.Conversion;
using DropWheel.Core.Wheel;

namespace DropWheel.Engines.Tools;

/// <summary>
/// Turns the image a quarter clockwise from how it shows, so a photo stored turned comes out as it was seen
/// turned once more; a recipe may ask for a half or three-quarter turn. The pixels are encoded again; a JPEG
/// keeps its quality.
/// </summary>
public sealed class RotateRightTool(TimeProvider? time = null) : ImageTool(time)
{
    public const string DegreesOption = "degrees";

    public override string Id => "tool.rotate-right";

    public override string Label => L.T("右转");

    public override string Hint => "90°";

    public override string? Glyph => "\uE7AD"; // Rotate

    protected override IReadOnlyCollection<string> Formats => Image.MagickImageConverter.Formats.Keys;

    protected override string Suffix => L.T("-旋转");

    /// <summary>degrees: clockwise, 90 by default.</summary>
    public override IReadOnlyList<OptionSpec> Options { get; } = [OptionSpec.OneOf(DegreesOption, L.T("顺时针转多少度，默认 90"), "90", "180", "270")];

    protected override Rendered? Render(string source, string formatId, ConversionRequest request)
    {
        var degrees = RequestOptions.Number(request, DegreesOption, 90);
        using var frames = Frames(source);
        foreach (var image in frames)
        {
            image.AutoOrient();
            image.Rotate(degrees);
            image.ResetPage();
        }

        return new(frames.ToByteArray(MagickFormatOf(formatId)), (int)frames[0].Width, (int)frames[0].Height);
    }
}
