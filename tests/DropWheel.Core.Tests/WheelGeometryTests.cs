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
