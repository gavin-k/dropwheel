using System.Runtime.InteropServices;
using DropWheel.App.Diagnostics;
using DropWheel.App.Onboarding;
using DropWheel.App.Overlay;
using DropWheel.App.Queue;
using DropWheel.App.Settings;
using DropWheel.App.Tray;
using DropWheel.Core.Actions;
using DropWheel.Core.Conversion;
using DropWheel.Core.Input;
using DropWheel.Core.Jobs;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Archive;
using DropWheel.Engines.Image;
using DropWheel.Engines.Media;
using DropWheel.Engines.Pdf;
using DropWheel.Engines.Subtitle;
using DropWheel.Engines.Tools;
using DropWheel.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace DropWheel.App;

/// <summary>
/// Composition: the conversion engines and file actions, job queue with Recycle Bin undo, the hook thread
/// and drop target host from DropWheel.Shell, the wheel overlay, the task, settings and first-run guide windows and the
/// tray icon. Key changes made in Settings reach the hooks at once. One instance per session; launching
/// again shows the task window (the guide, until it has been seen), <c>DropWheel.exe --quit</c> exits
/// the running one.
/// The app lives in the tray: closing the task window hides it, the tray menu's 退出 quits.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Lives as long as the process; Shutdown disposes everything.")]
public partial class App : Application
{
    private SingleInstance? _instance;
    private TrayIcon? _tray;
    private QueueWindow? _queueWindow;
    private SettingsWindow? _settingsWindow;
    private OnboardingWindow? _onboarding;
    private bool _shuttingDown;
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
        var commandLine = Environment.GetCommandLineArgs();
        var quit = commandLine.Contains("--quit");
        _instance = new SingleInstance();
        if (!_instance.IsFirst || quit)
        {
            if (_instance.IsFirst)
            {
                FileLog.Info("--quit: no DropWheel is running in this session");
            }
            else
            {
                _instance.Signal(quit);
                FileLog.Info($"another DropWheel is already running in this session; asked it to {(quit ? "quit" : "show its task window")}");
            }

            FileLog.Flush(TimeSpan.FromSeconds(1));
            _instance.Dispose();
            Exit();
            return;
        }

        try
        {
            FileLog.Info($"DropWheel {typeof(App).Assembly.GetName().Version} starting on {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture}");
            var ui = DispatcherQueue.GetForCurrentThread();

            var graph = new ConversionGraph();
            graph.Register(new MagickImageConverter());
            graph.Register(new SubtitleConverter());
            var archives = new SharpCompressArchiveConverter();
            graph.Register(archives);
            var ffmpeg = new FfmpegConverter();
            graph.Register(ffmpeg);
            graph.Register(new PdfConverter());
            _ = Task.Run(() =>
            {
                FileLog.Info(ffmpeg.Tools is { } tools ? $"ffmpeg sidecar: {tools.Ffmpeg}" : "ffmpeg sidecar not found; audio and video targets and video tools are greyed out");
                ffmpeg.Warmup();
            });
            var files = new ShellFileOperations();
            _queue = new JobQueue(graph, new UndoLedger(files));
            IReadOnlyList<IWheelOperation> operations = [.. FileActions.Create(files, new Desktop(ui)), .. EngineTools.Create(archives, ffmpeg)];
            var dataDir = ArgValue(commandLine, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropWheel");
            var settings = new SettingsStore(Path.Combine(dataDir, "settings.json"));
            var recent = new RecentItems(path: Path.Combine(dataDir, "recent.json"));
            foreach (var problem in new[] { settings.LoadProblem, recent.LoadProblem }.OfType<string>())
            {
                FileLog.Error(problem);
            }

            var builder = new WheelBuilder(graph, recent, new DefaultRecommender(graph), operations);

            var tapper = new ModifierTapper(settings.Current.Keys, TimeProvider.System);
            var winKey = new WinKeyGate(settings.Current.Keys);
            var typeAhead = new TypeAhead();
            _monitor = new DragMonitor(tapper, winKey, typeAhead);
            _monitor.Faulted += ex => FileLog.Error("hook thread handler failed", ex);
            _host = new DropTargetHost();

            var settingsWindow = _settingsWindow = new SettingsWindow(settings, operations, ui);
            var queueWindow = _queueWindow = new QueueWindow(_queue, ui, settingsWindow.Show);
            queueWindow.ShowKeys(settings.Current.Keys.KeysFor(WheelMode.Convert));
            settings.Changed += changed =>
            {
                tapper.Rebind(changed.Keys);
                winKey.Rebind(changed.Keys);
                queueWindow.ShowKeys(changed.Keys.KeysFor(WheelMode.Convert));
            };
            var onboarding = _onboarding = new OnboardingWindow(settings, _queue, ui, settingsWindow.Show);
            _wheel = new WheelWindow();
            _controller = new WheelController(ui, _monitor, _host, _wheel, builder, _queue, recent, settings, tapper, typeAhead);

            _host.Start();
            _monitor.Start();
            _tray = new TrayIcon(queueWindow.Handle, "DropWheel", Path.Combine(AppContext.BaseDirectory, "Assets", "DropWheel.ico"),
            [
                ("打开任务窗口", () => queueWindow.Show()),
                ("设置", settingsWindow.Show),
                ("入门指南", onboarding.Show),
                ("退出 DropWheel", Shutdown),
            ]);
            _tray.Selected += () => queueWindow.Show();
            void ShowHome()
            {
                if (settings.Current.OnboardingDone)
                {
                    queueWindow.Show();
                }
                else
                {
                    onboarding.Show();
                }
            }

            _instance.Listen(show: () => ui.TryEnqueue(ShowHome), quit: () => ui.TryEnqueue(Shutdown));

            // A user who has not seen the guide gets it, unless Windows started the app at sign-in (the
            // startup task in Package.appxmanifest): then only the tray.
            var atSignIn = AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
            if (atSignIn)
            {
                FileLog.Info("started by Windows at sign-in");
            }
            else if (!settings.Current.OnboardingDone)
            {
                onboarding.Show();
            }

            await _wheel.PrewarmAsync().ConfigureAwait(true);
            FileLog.Info($"ready: hooks and drop target up, log {FileLog.CurrentFile}, settings {dataDir}");

            if (Array.IndexOf(commandLine, "--wheel-preview") is var preview and >= 0)
            {
                await PreviewAsync(builder, settings.Current, withHost: commandLine.ElementAtOrDefault(preview + 1) == "host").ConfigureAwait(true);
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
    private async Task PreviewAsync(WheelBuilder builder, AppSettings settings, bool withHost)
    {
        var bounds = Microsoft.UI.Windowing.DisplayArea.Primary.OuterBounds;
        var (x, y, scale) = WheelController.Place((bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        _wheel!.ShowAt(x, y, scale);
        _wheel.SetModel(builder.Build(["preview.jpg"], WheelMode.Convert, settings));
        _wheel.SetHovered(0);
        if (withHost)
        {
            _host!.Show(new WheelPlacement(x, y, scale, [true, true, true, true, true, true]));
        }

        FileLog.Info($"preview: wheel at {x},{y} scale {scale:0.##}{(withHost ? " under the drop target host" : "")}");
        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
        Shutdown();
    }

    private static string? ArgValue(string[] args, string name) =>
        Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

    private void Shutdown()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        FileLog.Info("shutting down");
        _tray?.Dispose();
        _controller?.Dispose();
        _monitor?.Dispose();
        _host?.Dispose();
        _wheel?.Close();
        _settingsWindow?.Close();
        _onboarding?.Close();
        _queueWindow?.Close();
        if (_queue is not null && !_queue.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)))
        {
            FileLog.Info("a job was still running at exit");
        }

        FileLog.Flush(TimeSpan.FromSeconds(1));
        _instance?.Dispose();
        Exit();
    }
}
