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

    /// <summary>Offset of the middle of sector <paramref name="index"/>, halfway between hub and rim.</summary>
    public static (double X, double Y) SectorCenter(int index)
    {
        var a = (StartAngle(index) + SectorSweep / 2) * Math.PI / 180;
        var r = (InnerRadius + OuterRadius) / 2;
        return (Math.Cos(a) * r, Math.Sin(a) * r);
    }
}
