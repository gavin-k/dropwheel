namespace DropWheel.Core.Wheel;

/// <summary>
/// Hit-testing for one showing of the wheel. Near a screen edge the wheel is pulled into the work area,
/// so it opens off-centre and a sector can already be under the pointer; releasing straight away would
/// convert to a format nobody picked. When the pointer did not start on the hub, sectors stay inert
/// until it has been on the hub once.
/// </summary>
public sealed class SectorGate(bool startsOnHub)
{
    /// <summary>Whether sectors respond: from the start when the pointer opened on the hub, otherwise once it got there.</summary>
    public bool IsOpen { get; private set; } = startsOnHub;

    /// <summary>
    /// <see cref="WheelGeometry.SectorAt"/> for an offset in DIPs, but -1 while the gate is shut.
    /// <paramref name="opened"/> is true on the one call that opens it.
    /// </summary>
    public int SectorAt(double dx, double dy, out bool opened) => HitAt(dx, dy, null, out opened) is { Ring: WheelRing.Sector } hit ? hit.Index : -1;

    /// <summary>
    /// What is under an offset in DIPs: a sector, an item of the open <paramref name="ring"/>, the hub, or
    /// nothing; sectors and ring items are nothing while the gate is shut. <paramref name="opened"/> is true
    /// on the one call that opens it.
    /// </summary>
    public WheelHit HitAt(double dx, double dy, RingPlacement? ring, out bool opened)
    {
        var onHub = WheelGeometry.OnHub(dx, dy);
        opened = !IsOpen && onHub;
        IsOpen |= opened;
        if (onHub)
        {
            return WheelHit.Hub;
        }

        if (!IsOpen)
        {
            return WheelHit.None;
        }

        if (WheelGeometry.SectorAt(dx, dy) is var sector and >= 0)
        {
            return WheelHit.Sector(sector);
        }

        return ring is not null && WheelGeometry.ChildAt(dx, dy, ring.Parent, ring.Count) is var child and >= 0 ? WheelHit.Child(child) : WheelHit.None;
    }
}
