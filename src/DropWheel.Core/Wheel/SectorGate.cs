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
    public int SectorAt(double dx, double dy, out bool opened)
    {
        opened = !IsOpen && WheelGeometry.OnHub(dx, dy);
        IsOpen |= opened;
        return IsOpen ? WheelGeometry.SectorAt(dx, dy) : -1;
    }
}
