using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class WheelGeometryTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -40)]
    [InlineData(0, -160)]
    [InlineData(200, 200)]
    public void Hub_and_outside_are_no_sector(double dx, double dy)
        => WheelGeometry.SectorAt(dx, dy).Should().Be(-1);

    [Theory]
    [InlineData(0, -100, 0)]
    [InlineData(80, -40, 1)]
    [InlineData(80, 40, 2)]
    [InlineData(0, 100, 3)]
    [InlineData(-80, 40, 4)]
    [InlineData(-80, -40, 5)]
    public void Sectors_run_clockwise_from_twelve_o_clock(double dx, double dy, int expected)
        => WheelGeometry.SectorAt(dx, dy).Should().Be(expected);

    [Fact]
    public void Sector_centres_hit_their_own_sector()
    {
        for (var i = 0; i < WheelModel.SlotCount; i++)
        {
            var (x, y) = WheelGeometry.SectorCenter(i);
            WheelGeometry.SectorAt(x, y).Should().Be(i);
        }
    }

    [Fact]
    public void Rim_and_hub_edges_belong_to_the_ring()
    {
        WheelGeometry.SectorAt(0, -WheelGeometry.OuterRadius).Should().Be(0);
        WheelGeometry.SectorAt(0, -WheelGeometry.InnerRadius).Should().Be(0);
    }
}

public class RingGeometryTests
{
    [Fact]
    public void Ring_items_are_centred_on_their_sector_and_run_clockwise()
    {
        // Three items on sector 0 (12 o'clock): the middle one straight up, the first to the left, the last to the right.
        var (x0, y0) = WheelGeometry.ChildCenter(0, 3, 0);
        var (x1, y1) = WheelGeometry.ChildCenter(0, 3, 1);
        var (x2, _) = WheelGeometry.ChildCenter(0, 3, 2);

        x1.Should().BeApproximately(0, 1e-9);
        y1.Should().BeApproximately(-(WheelGeometry.RingInnerRadius + WheelGeometry.RingOuterRadius) / 2, 1e-9);
        x0.Should().BeNegative();
        y0.Should().BeNegative();
        x2.Should().BePositive();
        WheelGeometry.ChildStartAngle(0, 3, 0).Should().BeApproximately(-135, 1e-9);
    }

    [Fact]
    public void Ring_item_centres_hit_their_own_item_on_every_sector()
    {
        for (var parent = 0; parent < WheelModel.SlotCount; parent++)
        {
            for (var count = 1; count <= 6; count++)
            {
                for (var i = 0; i < count; i++)
                {
                    var (x, y) = WheelGeometry.ChildCenter(parent, count, i);
                    WheelGeometry.ChildAt(x, y, parent, count).Should().Be(i, $"parent {parent}, {count} items, item {i}");
                    WheelGeometry.SectorAt(x, y).Should().Be(-1, "the ring is outside the sectors");
                }
            }
        }
    }

    [Fact]
    public void Beyond_the_items_the_ring_band_and_the_gap_hit_nothing()
    {
        var (x, y) = WheelGeometry.ChildCenter(3, 2, 1);
        var (farX, farY) = WheelGeometry.ChildCenter(3, 6, 5);

        WheelGeometry.ChildAt(farX, farY, 3, 2).Should().Be(-1, "only two items are open");
        WheelGeometry.ChildAt(x, y, 3, 0).Should().Be(-1, "no items");
        WheelGeometry.ChildAt(0, WheelGeometry.RingOuterRadius + 1, 3, 2).Should().Be(-1, "outside the ring");
        WheelGeometry.ChildAt(0, WheelGeometry.OuterRadius + 1, 3, 2).Should().Be(-1, "in the gap between the sectors and the ring");
    }
}
