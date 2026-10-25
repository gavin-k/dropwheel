using ImageMagick;

namespace DropWheel.Engines.Tools;

/// <summary>Halves the width and height, turned the right way up first. The copy is named with its new size.</summary>
public sealed class HalfSizeTool(TimeProvider? time = null) : ImageTool(time)
{
    public override string Id => "tool.half-size";

    public override string Label => "缩小";

    public override string Hint => "一半尺寸";

    protected override IReadOnlyCollection<string> Formats => Image.MagickImageConverter.Formats.Keys;

    protected override string Suffix => "-{w}x{h}";

    protected override Rendered? Render(string source, string formatId)
    {
        using var frames = Frames(source);
        foreach (var image in frames)
        {
            image.AutoOrient();
            image.Resize(new MagickGeometry(Half(image.Width), Half(image.Height)) { IgnoreAspectRatio = true });
            image.ResetPage();
        }

        return new(frames.ToByteArray(MagickFormatOf(formatId)), (int)frames[0].Width, (int)frames[0].Height);
    }

    private static uint Half(uint size) => Math.Max(1, (size + 1) / 2);
}
