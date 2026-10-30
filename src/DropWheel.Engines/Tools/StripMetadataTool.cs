namespace DropWheel.Engines.Tools;

/// <summary>
/// Takes out where and when a photo was taken, the camera, the editing history and any text, without
/// encoding the pixels again; see <see cref="ImageMetadata"/> for what stays.
/// </summary>
public sealed class StripMetadataTool(TimeProvider? time = null) : ImageTool(time)
{
    public override string Id => "tool.strip-metadata";

    public override string Label => L.T("去元数据");

    public override string Hint => L.T("位置等信息");

    public override string? Glyph => "\uE75C"; // EraseTool

    protected override IReadOnlyCollection<string> Formats => ImageMetadata.Formats;

    protected override string Suffix => L.T("-无元数据");

    protected override string Unchanged => L.T("没有可去除的元数据");

    protected override Rendered? Render(string source, string formatId) =>
        ImageMetadata.Strip(File.ReadAllBytes(source), formatId) is { } data ? new(data) : null;
}
