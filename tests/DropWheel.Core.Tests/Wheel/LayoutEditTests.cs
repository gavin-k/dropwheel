using DropWheel.Core.Conversion;
using DropWheel.Core.Tools;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests.Wheel;

/// <summary>The edits the layout editor makes, on the image Tools wheel unless said otherwise.</summary>
public sealed class LayoutEditTests
{
    private static readonly IReadOnlyList<IWheelOperation> Ops =
    [
        .. new[] { "tool.a", "tool.b", "tool.c", "tool.d" }.Select(id => new FakeOperation(id, WheelMode.Tools, [MediaKind.Image])),
        new FakeOperation("user.x", WheelMode.Tools, [MediaKind.Image]),
        new FakeOperation("user.y", WheelMode.Tools, [MediaKind.Image]),
        new FakeOperation("user.z", WheelMode.Tools, [MediaKind.Image]),
    ];

    private const WheelMode Tools = WheelMode.Tools;
    private const MediaKind Image = MediaKind.Image;

    private static IEnumerable<string?> Ids(WheelLayout layout) =>
        layout.SlotsFor(Tools, Image)!.Select(s => s?.Item ?? (s?.Group is { } g ? g.Name.Zh + "[" + string.Join(",", g.Items) + "]" : null));

    [Fact]
    public void A_first_edit_starts_from_the_registration_order()
    {
        var (layout, placed) = WheelLayout.Empty.Put(Tools, Image, Ops, "user.z", 5);

        placed.Should().BeTrue();
        Ids(layout).Should().Equal("tool.a", "tool.b", "tool.c", "tool.d", "user.x", "更多[user.y,user.z]");
        layout.SlotsFor(Tools, MediaKind.Pdf).Should().BeNull("only the wheel edited is laid out");
    }

    [Fact]
    public void An_item_dropped_on_an_empty_sector_moves_there_and_on_an_item_makes_a_group()
    {
        var layout = WheelLayout.Empty.Remove(Tools, Image, Ops, "tool.d");
        Ids(layout).Should().Equal("tool.a", "tool.b", "tool.c", null, "user.x", "user.y");

        (layout, _) = layout.Put(Tools, Image, Ops, "tool.a", 3);
        Ids(layout).Should().Equal(null, "tool.b", "tool.c", "tool.a", "user.x", "user.y");

        (layout, _) = layout.Put(Tools, Image, Ops, "user.y", 1);
        Ids(layout).Should().Equal(null, "更多[tool.b,user.y]", "tool.c", "tool.a", "user.x", null);

        var (same, ok) = layout.Put(Tools, Image, Ops, "tool.c", 2);
        ok.Should().BeTrue();
        same.Should().BeSameAs(layout, "dropping an item on its own sector changes nothing");
    }

    [Fact]
    public void An_item_dropped_on_a_group_joins_it_and_a_full_group_refuses()
    {
        var (layout, _) = WheelLayout.Empty.Put(Tools, Image, Ops, "user.y", 0);
        Ids(layout).Should().Equal("更多[tool.a,user.y]", "tool.b", "tool.c", "tool.d", "user.x", null);

        (layout, _) = layout.Put(Tools, Image, Ops, "user.z", 0);
        Ids(layout).Should().Equal("更多[tool.a,user.y,user.z]", "tool.b", "tool.c", "tool.d", "user.x", null);

        foreach (var id in new[] { "tool.b", "tool.c", "tool.d" })
        {
            (layout, _) = layout.Put(Tools, Image, Ops, id, 0);
        }

        layout.SlotsFor(Tools, Image)![0]!.Group!.IsFull.Should().BeTrue();
        var (refused, ok) = layout.Put(Tools, Image, Ops, "user.x", 0);
        ok.Should().BeFalse();
        refused.Should().BeSameAs(layout);
        Ids(layout).Should().Equal("更多[tool.a,user.y,user.z,tool.b,tool.c,tool.d]", null, null, null, "user.x", null);
    }

    [Fact]
    public void Taking_an_item_out_of_a_group_of_two_leaves_a_group_of_one_and_out_of_a_group_of_one_leaves_nothing()
    {
        var (layout, _) = WheelLayout.Empty.Put(Tools, Image, Ops, "user.y", 0);
        (layout, _) = layout.Put(Tools, Image, Ops, "tool.a", 5);
        Ids(layout).Should().Equal("更多[user.y]", "tool.b", "tool.c", "tool.d", "user.x", "tool.a");

        layout = layout.Remove(Tools, Image, Ops, "user.y");
        Ids(layout).Should().Equal(null, "tool.b", "tool.c", "tool.d", "user.x", "tool.a");
        layout.Remove(Tools, Image, Ops, "user.nowhere").Should().BeSameAs(layout);
    }

    [Fact]
    public void Swapping_exchanges_two_sectors_whatever_they_hold()
    {
        var (layout, _) = WheelLayout.Empty.Put(Tools, Image, Ops, "user.y", 0);
        layout = layout.Remove(Tools, Image, Ops, "tool.d");

        layout = layout.Swap(Tools, Image, Ops, 0, 3);
        Ids(layout).Should().Equal(null, "tool.b", "tool.c", "更多[tool.a,user.y]", "user.x", null);
        layout.Swap(Tools, Image, Ops, 2, 2).Should().BeSameAs(layout);
        layout.Swap(Tools, Image, Ops, 2, 6).Should().BeSameAs(layout);
    }

    [Fact]
    public void Ungrouping_needs_room_for_every_item_but_the_first()
    {
        var (layout, _) = WheelLayout.Empty.Put(Tools, Image, Ops, "user.y", 0);
        (layout, _) = layout.Put(Tools, Image, Ops, "user.z", 0);
        Ids(layout).Should().Equal("更多[tool.a,user.y,user.z]", "tool.b", "tool.c", "tool.d", "user.x", null);

        var (refused, done) = layout.Ungroup(Tools, Image, Ops, 0);
        done.Should().BeFalse("two items need two empty sectors and there is one");
        refused.Should().BeSameAs(layout);

        layout = layout.Remove(Tools, Image, Ops, "tool.c");
        (layout, done) = layout.Ungroup(Tools, Image, Ops, 0);
        done.Should().BeTrue();
        Ids(layout).Should().Equal("tool.a", "tool.b", "user.y", "tool.d", "user.x", "user.z");
        layout.Ungroup(Tools, Image, Ops, 1).Done.Should().BeFalse("not a group");
    }

    [Fact]
    public void A_group_can_be_renamed_and_a_blank_name_is_ignored()
    {
        var (layout, _) = WheelLayout.Empty.Put(Tools, Image, Ops, "user.y", 0);

        layout = layout.RenameGroup(Tools, Image, Ops, 0, new("我的", "Mine"));
        layout.SlotsFor(Tools, Image)![0]!.Group!.Name.Should().Be(new LocalizedText("我的", "Mine"));
        layout.RenameGroup(Tools, Image, Ops, 0, new(" ", "")).Should().BeSameAs(layout);
        layout.RenameGroup(Tools, Image, Ops, 1, new("x", "x")).Should().BeSameAs(layout);
    }

    [Fact]
    public void Reset_forgets_one_wheel_and_leaves_the_others()
    {
        var (layout, _) = WheelLayout.Empty.Put(Tools, Image, Ops, "user.y", 0);
        (layout, _) = layout.Put(Tools, MediaKind.Pdf, Ops, "user.y", 0);

        layout = layout.Reset(Tools, Image);
        layout.SlotsFor(Tools, Image).Should().BeNull();
        layout.SlotsFor(Tools, MediaKind.Pdf).Should().NotBeNull();
        layout.Reset(Tools, Image).Should().BeSameAs(layout);

        layout = layout.Reset(Tools, MediaKind.Pdf);
        layout.Wheels.Should().BeEmpty();
    }
}
