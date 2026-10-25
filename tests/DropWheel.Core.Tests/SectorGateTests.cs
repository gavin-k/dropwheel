using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class SectorGateTests
{
    private static readonly (double X, double Y) OnSector0 = WheelGeometry.SectorCenter(0);
    private static readonly (double X, double Y) OnSector3 = WheelGeometry.SectorCenter(3);

    [Fact]
    public void Starting_on_the_hub_hit_tests_sectors_straight_away()
    {
        var gate = new SectorGate(startsOnHub: true);

        gate.SectorAt(OnSector3.X, OnSector3.Y, out var opened).Should().Be(3);
        opened.Should().BeFalse("it was never shut");
        gate.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Starting_off_the_hub_keeps_sectors_inert_until_the_hub()
    {
        var gate = new SectorGate(startsOnHub: false);

        gate.SectorAt(OnSector0.X, OnSector0.Y, out var opened).Should().Be(-1);
        opened.Should().BeFalse();
        gate.SectorAt(OnSector3.X, OnSector3.Y, out _).Should().Be(-1, "crossing sectors on the way does not count");
        gate.IsOpen.Should().BeFalse();

        gate.SectorAt(5, -5, out opened).Should().Be(-1, "the hub itself is never a sector");
        opened.Should().BeTrue();
        gate.SectorAt(OnSector0.X, OnSector0.Y, out opened).Should().Be(0);
        opened.Should().BeFalse("it opens once");
    }

    [Fact]
    public void The_hub_edge_belongs_to_the_sector()
    {
        var gate = new SectorGate(startsOnHub: false);

        gate.SectorAt(0, -WheelGeometry.InnerRadius, out var opened).Should().Be(-1);
        opened.Should().BeFalse();
        WheelGeometry.OnHub(0, -WheelGeometry.InnerRadius + 0.5).Should().BeTrue();
        WheelGeometry.OnHub(0, -WheelGeometry.InnerRadius).Should().BeFalse();
    }
}
