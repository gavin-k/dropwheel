namespace DropWheel.Core.Tests;

/// <summary>A fact that only holds on Windows; elsewhere it is reported as skipped, not as passed.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows file identity only.";
        }
    }
}
