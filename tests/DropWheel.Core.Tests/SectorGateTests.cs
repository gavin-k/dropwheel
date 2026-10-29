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

public class RingHitTests
{
    private static readonly (double X, double Y) OnSector3 = WheelGeometry.SectorCenter(3);

    [Fact]
    public void A_ring_item_is_hit_only_while_the_ring_is_open()
    {
        var gate = new SectorGate(startsOnHub: true);
        var (x, y) = WheelGeometry.ChildCenter(3, 2, 1);

        gate.HitAt(x, y, null, out _).Should().Be(WheelHit.None);
        gate.HitAt(x, y, new RingPlacement(3, [true, false]), out _).Should().Be(WheelHit.Child(1));
        gate.HitAt(x, y, new RingPlacement(0, [true, false]), out _).Should().Be(WheelHit.None, "the ring is open on another sector");
        gate.HitAt(OnSector3.X, OnSector3.Y, new RingPlacement(3, [true, false]), out _).Should().Be(WheelHit.Sector(3));
        gate.HitAt(0, 0, new RingPlacement(3, [true, false]), out _).Should().Be(WheelHit.Hub);
    }

    [Fact]
    public void A_shut_gate_reports_the_hub_but_no_sector_or_item()
    {
        var gate = new SectorGate(startsOnHub: false);
        var ring = new RingPlacement(3, [true]);
        var (x, y) = WheelGeometry.ChildCenter(3, 1, 0);

        gate.HitAt(x, y, ring, out var opened).Should().Be(WheelHit.None);
        opened.Should().BeFalse();
        gate.HitAt(OnSector3.X, OnSector3.Y, ring, out _).Should().Be(WheelHit.None);
        gate.HitAt(1, 1, ring, out opened).Should().Be(WheelHit.Hub);
        opened.Should().BeTrue();
        gate.HitAt(x, y, ring, out opened).Should().Be(WheelHit.Child(0));
        opened.Should().BeFalse();
        new RingPlacement(3, [true, false]).IsEnabled(1).Should().BeFalse();
        new RingPlacement(3, [true, false]).IsEnabled(2).Should().BeFalse();
        WheelHit.Child(0).ToString().Should().Be("child 0");
    }
}
