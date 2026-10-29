namespace DropWheel.Core.Wheel;

/// <summary>Which part of the wheel a point is on.</summary>
public enum WheelRing
{
    /// <summary>Off the wheel, or in the gap between the sectors and an open ring.</summary>
    None,

    /// <summary>The hub in the middle.</summary>
    Hub,

    /// <summary>One of the six sectors; the index is the sector's.</summary>
    Sector,

    /// <summary>An item of the open ring; the index is the item's place in the ring.</summary>
    Child,
}

/// <summary>Where the pointer is on the wheel; equal hits are the same place, so a move within one is not reported.</summary>
public readonly record struct WheelHit(WheelRing Ring, int Index)
{
    public static WheelHit None { get; } = new(WheelRing.None, -1);

    public static WheelHit Hub { get; } = new(WheelRing.Hub, -1);

    public static WheelHit Sector(int index) => new(WheelRing.Sector, index);

    public static WheelHit Child(int index) => new(WheelRing.Child, index);

    public override string ToString() => Ring switch
    {
        WheelRing.Sector => $"sector {Index}",
        WheelRing.Child => $"child {Index}",
        WheelRing.Hub => "hub",
        _ => "none",
    };
}

/// <summary>The ring open on a sector: the items of the group there, and which of them can take a drop.</summary>
public sealed record RingPlacement(int Parent, IReadOnlyList<bool> Enabled)
{
    public int Count => Enabled.Count;

    public bool IsEnabled(int index) => index >= 0 && index < Enabled.Count && Enabled[index];
}
