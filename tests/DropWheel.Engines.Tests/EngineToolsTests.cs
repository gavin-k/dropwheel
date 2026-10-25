using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Archive;
using DropWheel.Engines.Media;
using DropWheel.Engines.Ocr;
using DropWheel.Engines.Tools;
using FluentAssertions;

namespace DropWheel.Engines.Tests;

public sealed class EngineToolsTests
{
    private static readonly WheelBuilder Builder = new(
        new ConversionGraph(), new RecentItems(), new DefaultRecommender(new ConversionGraph()), EngineTools.Create(new SharpCompressArchiveConverter(), new FfmpegConverter()));

    private static WheelModel Wheel(params string[] paths) => Builder.Build(paths, WheelMode.Tools, AppSettings.Default);

    [Fact]
    public void Each_kind_of_file_sees_its_own_tools_in_a_fixed_order()
    {
        Wheel("a.jpg").Slots.Select(s => s.ItemId).Should().Equal(
            "tool.compress-image", "tool.half-size", "tool.rotate-right", "tool.strip-metadata", "tool.combine-pdf");
        Wheel("a.pdf").Slots.Select(s => s.ItemId).Should().Equal("tool.merge-pdf", "tool.split-pdf");
        Wheel("a.mp4").Slots.Select(s => s.ItemId).Should().Equal("tool.compress-video", "tool.extract-audio", "tool.mute");
        Wheel("a.zip").Slots.Select(s => s.ItemId).Should().Equal("tool.extract");
        Wheel("a.mp3").Slots.Should().BeEmpty();
    }

    [Fact]
    public void With_a_recognizer_images_and_pdfs_end_with_reading_their_text()
    {
        var builder = new WheelBuilder(new ConversionGraph(), new RecentItems(), new DefaultRecommender(new ConversionGraph()),
            EngineTools.Create(new SharpCompressArchiveConverter(), new FfmpegConverter(), recognizer: new NoText()));

        builder.Build(["a.jpg"], WheelMode.Tools, AppSettings.Default).Slots.Select(s => s.ItemId).Should().Equal(
            "tool.compress-image", "tool.half-size", "tool.rotate-right", "tool.strip-metadata", "tool.combine-pdf", "tool.ocr");
        builder.Build(["a.pdf"], WheelMode.Tools, AppSettings.Default).Slots.Select(s => s.ItemId).Should().Equal("tool.merge-pdf", "tool.split-pdf", "tool.ocr");
    }

    [Fact]
    public void Ids_are_unique_and_none_is_a_format()
    {
        var ids = EngineTools.Create(new SharpCompressArchiveConverter(), new FfmpegConverter(), recognizer: new NoText()).Select(t => t.Id).ToList();

        ids.Should().OnlyHaveUniqueItems().And.OnlyContain(id => id.StartsWith("tool.", StringComparison.Ordinal));
        ids.Should().OnlyContain(id => FormatRegistry.FromExtension(id) == null);
    }

    private sealed class NoText : ITextRecognizer
    {
        public string? Unavailable => null;

        public int MaxSide => 100;

        public string Language => "";

        public Task<IReadOnlyList<IReadOnlyList<string>>> ReadAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>([]);
    }
}
