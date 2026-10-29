namespace DropWheel.Core.Wheel;

/// <summary>
/// Shape of the six-sector wheel in DIPs, shared by the XAML that draws it and the drop target that
/// hit-tests it. Sector 0 is centred on 12 o'clock and indices run clockwise; angles follow screen
/// coordinates (0° = 3 o'clock, y grows downwards).
/// </summary>
public static class WheelGeometry
{
    public const double OuterRadius = 146;
    public const double InnerRadius = 54;
    public const double SectorSweep = 360.0 / WheelModel.SlotCount;

    /// <summary>The outer ring a group opens into: its items sit between these radii, each <see cref="ChildSweep"/> wide.</summary>
    public const double RingInnerRadius = 152;
    public const double RingOuterRadius = 232;
    public const double ChildSweep = 30;

    /// <summary>Room around the ring, in DIPs, when the window is sized for it.</summary>
    public const double RingMargin = 16;

    /// <summary>Angle in degrees where sector <paramref name="index"/> starts.</summary>
    public static double StartAngle(int index) => index * SectorSweep - 90 - SectorSweep / 2;

    /// <summary>Sector under an offset from the wheel centre, or -1 on the hub and outside the rim.</summary>
    public static int SectorAt(double dx, double dy)
    {
        var r = Math.Sqrt(dx * dx + dy * dy);
        if (r < InnerRadius || r > OuterRadius)
        {
            return -1;
        }

        var deg = Math.Atan2(dy, dx) * 180 / Math.PI - StartAngle(0);
        deg = ((deg % 360) + 360) % 360;
        return Math.Min((int)(deg / SectorSweep), WheelModel.SlotCount - 1);
    }

    /// <summary>True when an offset from the wheel centre is on the hub, where <see cref="SectorAt"/> gives -1.</summary>
    public static bool OnHub(double dx, double dy) => dx * dx + dy * dy < InnerRadius * InnerRadius;

    /// <summary>Angle in degrees where item <paramref name="index"/> of a <paramref name="count"/>-item ring on sector <paramref name="parent"/> starts; the items are centred on the sector's middle.</summary>
    public static double ChildStartAngle(int parent, int count, int index) =>
        StartAngle(parent) + SectorSweep / 2 - count * ChildSweep / 2 + index * ChildSweep;

    /// <summary>The ring item under an offset from the wheel centre, or -1 off the ring or outside the items.</summary>
    public static int ChildAt(double dx, double dy, int parent, int count)
    {
        var r = Math.Sqrt(dx * dx + dy * dy);
        if (count <= 0 || r < RingInnerRadius || r > RingOuterRadius)
        {
            return -1;
        }

        var deg = Math.Atan2(dy, dx) * 180 / Math.PI - ChildStartAngle(parent, count, 0);
        deg = ((deg % 360) + 360) % 360;
        var index = (int)(deg / ChildSweep);
        return index < count ? index : -1;
    }

    /// <summary>Offset of the middle of ring item <paramref name="index"/>, halfway across the ring.</summary>
    public static (double X, double Y) ChildCenter(int parent, int count, int index)
    {
        var a = (ChildStartAngle(parent, count, index) + ChildSweep / 2) * Math.PI / 180;
        var r = (RingInnerRadius + RingOuterRadius) / 2;
        return (Math.Cos(a) * r, Math.Sin(a) * r);
    }

    /// <summary>Offset of the middle of sector <paramref name="index"/>, halfway between hub and rim.</summary>
    public static (double X, double Y) SectorCenter(int index)
    {
        var a = (StartAngle(index) + SectorSweep / 2) * Math.PI / 180;
        var r = (InnerRadius + OuterRadius) / 2;
        return (Math.Cos(a) * r, Math.Sin(a) * r);
    }
}
