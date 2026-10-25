using DropWheel.Core.Wheel;
using DropWheel.Engines.Archive;

namespace DropWheel.Engines.Tools;

/// <summary>The Tools wheel's items, in sector order; each kind of file sees only its own.</summary>
public static class EngineTools
{
    public static IReadOnlyList<IWheelOperation> Create(SharpCompressArchiveConverter archives, TimeProvider? time = null) =>
    [
        new CompressImageTool(time),
        new HalfSizeTool(time),
        new RotateRightTool(time),
        new StripMetadataTool(time),
        new CombinePdfTool(time),
        new ExtractArchiveTool(archives, time),
    ];
}
