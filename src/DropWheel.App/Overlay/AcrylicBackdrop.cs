using DropWheel.App.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DropWheel.App.Overlay;

/// <summary>
/// Desktop acrylic for the wheel disc. The overlay is never activated, and acrylic falls back to a
/// solid colour for inactive windows, so the configuration pins IsInputActive = true
/// (spikes/AcrylicSpike, finding 2). With system transparency effects off the controller shows
/// <see cref="Fallback"/>, a solid dark surface.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "XAML owns the backdrop; the controller is disposed in OnTargetDisconnected.")]
internal sealed class AcrylicBackdrop : SystemBackdrop
{
    private static readonly Color Fallback = Color.FromArgb(255, 0x26, 0x26, 0x26);
    private DesktopAcrylicController? _controller;

    public DesktopAcrylicController? Controller => _controller;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Base,
            FallbackColor = Fallback,
            TintColor = Color.FromArgb(255, 0x20, 0x20, 0x20),
            TintOpacity = 0.5f,
            LuminosityOpacity = 0.75f,
        };
        _controller.StateChanged += (c, _) => FileLog.Info($"overlay: acrylic state {c.State}");
        _controller.SetSystemBackdropConfiguration(Configuration(connectedTarget, xamlRoot));
        _controller.AddSystemBackdropTarget(connectedTarget);
        FileLog.Info($"overlay: acrylic connected, state {_controller.State}, supported {DesktopAcrylicController.IsSupported()}");
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot) =>
        _controller?.SetSystemBackdropConfiguration(Configuration(target, xamlRoot));

    private SystemBackdropConfiguration Configuration(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot) => new()
    {
        IsInputActive = true,
        IsHighContrast = GetDefaultSystemBackdropConfiguration(target, xamlRoot).IsHighContrast,
        Theme = SystemBackdropTheme.Dark,
    };
}
