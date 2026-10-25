using DropWheel.Core.Wheel;
using DropWheel.Engines.Archive;
using DropWheel.Engines.Media;
using DropWheel.Engines.Ocr;

namespace DropWheel.Engines.Tools;

/// <summary>The Tools wheel's items, in sector order; each kind of file sees only its own.</summary>
public static class EngineTools
{
    /// <param name="recognizer">Reads text for 识别文字, which is left off without one.</param>
    public static IReadOnlyList<IWheelOperation> Create(SharpCompressArchiveConverter archives, FfmpegConverter ffmpeg, TimeProvider? time = null, ITextRecognizer? recognizer = null) =>
    [
        new CompressImageTool(time),
        new HalfSizeTool(time),
        new RotateRightTool(time),
        new StripMetadataTool(time),
        new CombinePdfTool(time),
        new MergePdfTool(time),
        new SplitPdfTool(time),
        .. recognizer is null ? [] : new IWheelOperation[] { new OcrTool(recognizer, time) },
        new CompressVideoTool(ffmpeg, time),
        new ExtractAudioTool(ffmpeg, time),
        new MuteVideoTool(ffmpeg, time),
        new ExtractArchiveTool(archives, time),
    ];
}
