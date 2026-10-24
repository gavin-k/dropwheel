using Microsoft.UI.Xaml;

namespace DropWheel.App;

/// <summary>
/// Single-instance tray-resident app. On launch: register engines, start DragMonitor + KeyboardHook,
/// show onboarding on first run. Windows (overlay / queue / settings) are created lazily.
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // TODO(M0):
        //  - Microsoft.Windows.AppLifecycle single-instance redirection (files from Explorer verb / CLI alias)
        //  - DI container: ConversionGraph + MagickImageConverter, JobQueue, Settings store
        //  - DragMonitor.Start(); KeyboardHook.Start(tapper, () => monitor.Phase == Dragging)
        //  - tapper.Invoked += mode => OverlayWindow.ShowWheel(mode, files?)
        //  - TrayIcon (H.NotifyIcon.WinUI or custom Shell_NotifyIcon)
        _ = args;
    }
}
