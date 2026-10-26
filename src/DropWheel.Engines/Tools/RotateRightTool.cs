namespace DropWheel.Engines.Tools;

/// <summary>
/// Turns the image a quarter clockwise from how it shows, so a photo stored turned comes out as it was seen
/// turned once more. The pixels are encoded again; a JPEG keeps its quality.
/// </summary>
public sealed class RotateRightTool(TimeProvider? time = null) : ImageTool(time)
{
    public override string Id => "tool.rotate-right";

    public override string Label => L.T("右转");

    public override string Hint => "90°";

    protected override IReadOnlyCollection<string> Formats => Image.MagickImageConverter.Formats.Keys;

    protected override string Suffix => L.T("-旋转");

    protected override Rendered? Render(string source, string formatId)
    {
        using var frames = Frames(source);
        foreach (var image in frames)
        {
            image.AutoOrient();
            image.Rotate(90);
            image.ResetPage();
        }

        return new(frames.ToByteArray(MagickFormatOf(formatId)), (int)frames[0].Width, (int)frames[0].Height);
    }
}
