using DropWheel.Core.Conversion;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Archive;
using DropWheel.Engines.Document;
using DropWheel.Engines.Image;
using DropWheel.Engines.Media;
using DropWheel.Engines.Ocr;
using DropWheel.Engines.Pdf;
using DropWheel.Engines.Sheet;
using DropWheel.Engines.Subtitle;
using DropWheel.Engines.Tools;

namespace DropWheel.App;

/// <summary>
/// The converters DropWheel ships, registered into one graph, and the built-in tools made from them. The app and
/// the tool command line (<see cref="Program"/>) both build this, so a recipe an agent validates from the console
/// is checked against the same steps and the same conversions the wheel will run.
/// </summary>
internal sealed class ConverterSet
{
    public ConverterSet()
    {
        Graph.Register(new MagickImageConverter());
        Graph.Register(new SubtitleConverter());
        Graph.Register(Archives);
        Graph.Register(Ffmpeg);
        Graph.Register(new PdfConverter());
        Graph.Register(new DocumentConverter());
        Graph.Register(new SheetConverter());
    }

    public ConversionGraph Graph { get; } = new();

    public SharpCompressArchiveConverter Archives { get; } = new();

    public FfmpegConverter Ffmpeg { get; } = new();

    /// <summary>The built-in tools; the file actions are the app's, since most need its windows.</summary>
    public IReadOnlyList<IWheelOperation> Tools(ITextRecognizer recognizer) => EngineTools.Create(Archives, Ffmpeg, recognizer: recognizer);
}
