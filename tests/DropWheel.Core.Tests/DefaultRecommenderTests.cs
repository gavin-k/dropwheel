using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class DefaultRecommenderTests
{
    private static ConversionGraph Graph(params string[] formats)
    {
        var graph = new ConversionGraph();
        graph.Register(new TestConverter(TestConverter.Between(formats)));
        return graph;
    }

    [Fact]
    public void Jpg_gets_the_image_preferences_without_itself()
    {
        var graph = Graph("jpg", "png", "webp", "avif", "gif", "bmp", "tiff");
        new DefaultRecommender(graph).For(["a.jpg"], MediaKind.Image, WheelMode.Convert)
            .Should().Equal("png", "webp", "avif", "gif", "tiff", "bmp");
    }

    [Fact]
    public void Png_is_offered_jpg_first()
    {
        var graph = Graph("jpg", "png", "webp");
        new DefaultRecommender(graph).For(["a.png"], MediaKind.Image, WheelMode.Convert)
            .Should().Equal("jpg", "webp");
    }

    [Fact]
    public void Unreachable_targets_are_dropped_and_extra_ones_follow()
    {
        var graph = new ConversionGraph();
        graph.Register(new TestConverter([new("jpg", "png"), new("jpg", "heic")]));
        new DefaultRecommender(graph).For(["a.jpg"], MediaKind.Image, WheelMode.Convert)
            .Should().Equal("png", "heic");
    }

    [Theory]
    [InlineData(WheelMode.Tools)]
    [InlineData(WheelMode.Actions)]
    public void Only_the_convert_wheel_has_recommendations_in_M0(WheelMode mode)
        => new DefaultRecommender(Graph("jpg", "png")).For(["a.jpg"], MediaKind.Image, mode).Should().BeEmpty();

    [Fact]
    public void Wheel_puts_recent_first_and_fills_with_recommendations()
    {
        var graph = Graph("jpg", "png", "webp", "avif", "gif", "bmp", "tiff");
        var recent = new RecentItems();
        recent.Touch(MediaKind.Image, WheelMode.Convert, "webp");

        var wheel = new WheelBuilder(graph, recent, new DefaultRecommender(graph)).Build(["/p/a.jpg"], WheelMode.Convert, AppSettings.Default);

        wheel.Slots.Select(s => s.ItemId).Should().Equal("webp", "png", "avif", "gif", "tiff", "bmp");
        wheel.Slots[0].SubLabel.Should().Be("最近");
        wheel.Slots[1].SubLabel.Should().Be("推荐");
        wheel.CenterTitle.Should().Be("a.jpg");
    }
}
