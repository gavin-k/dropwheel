using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class WheelSearchTests
{
    private readonly RecentItems _recent = new();
    private readonly WheelBuilder _builder;

    public WheelSearchTests()
    {
        var graph = new ConversionGraph();
        graph.Register(new TestConverter(TestConverter.Between("jpg", "png", "webp", "avif", "gif", "bmp", "tiff", "ico")));
        graph.Register(new TestConverter([new("jpg", "pdf"), new("png", "pdf"), new("jpg", "pdfa")]));
        _builder = new WheelBuilder(graph, _recent, new DefaultRecommender(graph));
    }

    private WheelModel Search(string query, string source = "/p/a.jpg") => _builder.Build([source], WheelMode.Convert, AppSettings.Default, query);

    [Fact]
    public void Without_a_search_the_wheel_says_there_are_more_targets_than_slots()
    {
        var wheel = Search("");

        wheel.Slots.Should().HaveCount(WheelModel.SlotCount);
        wheel.HasMore.Should().BeTrue();
        wheel.CenterTitle.Should().Be("a.jpg");
    }

    [Fact]
    public void Finds_a_target_the_wheel_had_no_room_for()
    {
        Search("").Slots.Select(s => s.ItemId).Should().NotContain("ico");

        var wheel = Search("ico");

        wheel.Slots.Select(s => s.ItemId).Should().Equal("ico");
        wheel.Slots[0].SubLabel.Should().Be("匹配");
        wheel.Slots[0].Source.Should().Be(SlotSource.Search);
        wheel.CenterTitle.Should().Be("搜索 ICO");
        wheel.CenterSubtitle.Should().Be("1 个格式");
        wheel.HasMore.Should().BeFalse();
    }

    [Fact]
    public void Puts_prefixes_before_other_matches_and_keeps_the_wheel_order_among_them()
    {
        Search("p").Slots.Select(s => s.ItemId).Should().Equal("png", "pdf", "pdfa", "webp", "bmp");
    }

    [Fact]
    public void Matches_aliases()
    {
        Search("tif").Slots.Select(s => s.ItemId).Should().Equal("tiff");
        Search("jpeg", "/p/a.png").Slots.Select(s => s.ItemId).Should().Equal("jpg");
    }

    [Fact]
    public void An_exact_match_comes_first()
    {
        _recent.Touch(MediaKind.Image, WheelMode.Convert, "pdfa");
        Search("").Slots[0].ItemId.Should().Be("pdfa");

        var wheel = Search("pdf");

        wheel.Slots.Select(s => s.ItemId).Should().Equal("pdf", "pdfa");
        wheel.Slots[1].SubLabel.Should().Be("匹配");
    }

    [Fact]
    public void Says_why_a_format_it_names_is_missing()
    {
        Search("docx").CenterSubtitle.Should().Be("转不了 DOCX");
        Search("jpg").CenterSubtitle.Should().Be("已经是 JPG");
        Search("zzz").CenterSubtitle.Should().Be("没有这个格式");
        Search("zzz").Slots.Should().BeEmpty();
    }

    [Fact]
    public void Other_wheels_ignore_a_search()
    {
        var wheel = _builder.Build(["/p/a.jpg"], WheelMode.Tools, AppSettings.Default, "bmp");

        wheel.CenterTitle.Should().Be("a.jpg");
    }
}
