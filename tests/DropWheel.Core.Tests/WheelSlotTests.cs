using DropWheel.Core.Conversion;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class WheelSlotTests
{
    private static WheelSlot Slot(string label, string subLabel, SlotSource source, Availability availability, QualityEstimate? quality = null) =>
        new(0, label.ToLowerInvariant(), label, subLabel, source, availability, quality);

    [Fact]
    public void An_unavailable_item_says_why_in_the_hub_and_to_screen_readers()
    {
        var slot = Slot("HEIC", "不可用", SlotSource.Search, Availability.Unavailable("HEIC 编码需要系统已安装「HEVC 视频扩展」"));

        slot.Detail.Should().Be(new SlotDetail("HEIC 不可用", "HEIC 编码需要系统已安装「HEVC 视频扩展」"));
        slot.AccessibleName.Should().Be("HEIC，匹配，不可用：HEIC 编码需要系统已安装「HEVC 视频扩展」");
    }

    [Fact]
    public void The_hub_shows_the_first_reason_and_screen_readers_get_them_all()
    {
        var slot = Slot("JPG", "推荐", SlotSource.Recommended, Availability.Available,
            new(QualityLevel.Medium, ["有损压缩，文字和线条边缘可能出现杂点", "透明部分变成白色"]));

        slot.Detail.Should().Be(new SlotDetail("预期质量中", "有损压缩，文字和线条边缘可能出现杂点"));
        slot.AccessibleName.Should().Be("JPG，推荐，预期质量中：有损压缩，文字和线条边缘可能出现杂点；透明部分变成白色");
    }

    [Fact]
    public void An_item_with_nothing_to_add_keeps_the_hub_as_it_is()
    {
        var slot = Slot("PNG", "最近", SlotSource.Recent, Availability.Available);

        slot.Detail.Should().BeNull();
        slot.AccessibleName.Should().Be("PNG，最近");
    }

    [Fact]
    public void An_action_turned_off_in_settings_names_the_setting()
    {
        var slot = Slot("删除", "未开启", SlotSource.Fixed, Availability.Unavailable("在设置里打开后才可用"));

        slot.Detail.Should().Be(new SlotDetail("删除 未开启", "在设置里打开后才可用"));
        slot.AccessibleName.Should().Be("删除，未开启：在设置里打开后才可用");
    }

    [Fact]
    public void A_tool_reads_its_hint()
    {
        Slot("缩小", "长边 50%", SlotSource.Fixed, Availability.Available).AccessibleName.Should().Be("缩小，长边 50%");
    }
}
