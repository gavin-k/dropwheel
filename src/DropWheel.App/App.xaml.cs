using System.Runtime.InteropServices;
using DropWheel.App.Diagnostics;
using DropWheel.App.Overlay;
using DropWheel.App.Queue;
using DropWheel.Core.Conversion;
using DropWheel.Core.Input;
using DropWheel.Core.Jobs;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Image;
using DropWheel.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DropWheel.App;

/// <summary>
/// M0 composition: Magick image engine, job queue with Recycle Bin undo, the hook thread and drop target
/// host from DropWheel.Shell, the wheel overlay and the queue window. One instance per session (named
/// mutex). Closing the queue window quits until there is a tray icon.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Lives as long as the process; Shutdown disposes everything.")]
public partial class App : Application
{
    private Mutex? _singleInstance;
    private DragMonitor? _monitor;
    private DropTargetHost? _host;
    private JobQueue? _queue;
    private WheelController? _controller;
    private WheelWindow? _wheel;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => FileLog.Error("unhandled exception on the UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => FileLog.Error("unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => FileLog.Error("unobserved task exception", e.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\DropWheel.App", out var first);
        if (!first)
        {
            FileLog.Info("another DropWheel is already running in this session; exiting");
            FileLog.Flush(TimeSpan.FromSeconds(1));
            Exit();
            return;
        }

        try
        {
            FileLog.Info($"DropWheel {typeof(App).Assembly.GetName().Version} starting on {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture}");
            var ui = DispatcherQueue.GetForCurrentThread();

            var graph = new ConversionGraph();
            graph.Register(new MagickImageConverter());
            _queue = new JobQueue(graph, new UndoLedger(new ShellFileOperations()));
            var recent = new RecentItems();
            var builder = new WheelBuilder(graph, recent, new DefaultRecommender(graph));

            // Convert only: binding Win or Ctrl+Alt to wheels M0 does not have would swallow the Win key mid-drag for nothing.
            var bindings = KeyBindings.Default with { Bindings = [.. KeyBindings.Default.Bindings.Where(b => b.Mode == WheelMode.Convert)] };
            var tapper = new ModifierTapper(bindings, TimeProvider.System);
            _monitor = new DragMonitor(tapper, new WinKeyGate(bindings));
            _monitor.Faulted += ex => FileLog.Error("hook thread handler failed", ex);
            _host = new DropTargetHost();

            var queueWindow = new QueueWindow(_queue, ui);
            queueWindow.Closed += (_, _) => Shutdown();
            _wheel = new WheelWindow();
            _controller = new WheelController(ui, _monitor, _host, _wheel, builder, _queue, recent, tapper);

            _host.Start();
            _monitor.Start();
            queueWindow.Show();
            await _wheel.PrewarmAsync().ConfigureAwait(true);
            FileLog.Info($"ready: hooks and drop target up, log {FileLog.CurrentFile}");

            var commandLine = Environment.GetCommandLineArgs();
            if (Array.IndexOf(commandLine, "--wheel-preview") is var preview and >= 0)
            {
                await PreviewAsync(builder, withHost: commandLine.ElementAtOrDefault(preview + 1) == "host").ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("startup failed", ex);
            Shutdown();
        }
    }

    /// <summary>
    /// Diagnostic (<c>--wheel-preview [host]</c>): shows the wheel for a JPG at the centre of the primary
    /// screen with the first sector hovered, optionally under the drop target host, then quits.
    /// </summary>
    private async Task PreviewAsync(WheelBuilder builder, bool withHost)
    {
        var bounds = Microsoft.UI.Windowing.DisplayArea.Primary.OuterBounds;
        var (x, y, scale) = WheelController.Place((bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        _wheel!.ShowAt(x, y, scale);
        _wheel.SetModel(builder.Build(["preview.jpg"], WheelMode.Convert, outputDirectory: ""));
        _wheel.SetHovered(0);
        if (withHost)
        {
            _host!.Show(new WheelPlacement(x, y, scale, [true, true, true, true, true, true]));
        }

        FileLog.Info($"preview: wheel at {x},{y} scale {scale:0.##}{(withHost ? " under the drop target host" : "")}");
        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
        Shutdown();
    }

    private void Shutdown()
    {
        FileLog.Info("shutting down");
        _controller?.Dispose();
        _monitor?.Dispose();
        _host?.Dispose();
        _wheel?.Close();
        if (_queue is not null && !_queue.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)))
        {
            FileLog.Info("a job was still running at exit");
        }

        FileLog.Flush(TimeSpan.FromSeconds(1));
        _singleInstance?.Dispose();
        Exit();
    }
}
