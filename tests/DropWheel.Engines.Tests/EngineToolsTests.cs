using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Archive;
using DropWheel.Engines.Tools;
using FluentAssertions;

namespace DropWheel.Engines.Tests;

public sealed class EngineToolsTests
{
    private static readonly WheelBuilder Builder = new(
        new ConversionGraph(), new RecentItems(), new DefaultRecommender(new ConversionGraph()), EngineTools.Create(new SharpCompressArchiveConverter()));

    private static WheelModel Wheel(params string[] paths) => Builder.Build(paths, WheelMode.Tools, AppSettings.Default);

    [Fact]
    public void Each_kind_of_file_sees_its_own_tools_in_a_fixed_order()
    {
        Wheel("a.jpg").Slots.Select(s => s.ItemId).Should().Equal(
            "tool.compress-image", "tool.half-size", "tool.rotate-right", "tool.strip-metadata", "tool.combine-pdf");
        Wheel("a.zip").Slots.Select(s => s.ItemId).Should().Equal("tool.extract");
        Wheel("a.mp4").Slots.Should().BeEmpty();
    }

    [Fact]
    public void Ids_are_unique_and_none_is_a_format()
    {
        var ids = EngineTools.Create(new SharpCompressArchiveConverter()).Select(t => t.Id).ToList();

        ids.Should().OnlyHaveUniqueItems().And.OnlyContain(id => id.StartsWith("tool.", StringComparison.Ordinal));
        ids.Should().OnlyContain(id => FormatRegistry.FromExtension(id) == null);
    }
}
