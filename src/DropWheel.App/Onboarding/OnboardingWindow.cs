using DropWheel.App.Diagnostics;
using DropWheel.Core.Jobs;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using ImageMagick;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DropWheel.App.Onboarding;

/// <summary>
/// The first-run guide: the three steps of a conversion with the keys and output place the user has now,
/// a 试一试 button that puts a sample photo in Pictures and selects it in Explorer, the outcome of
/// whatever is converted while the guide is open, and where the app lives once it is closed. 开始使用 and
/// the close button both mark the guide as seen, so it is not shown again at the next launch.
/// </summary>
internal sealed class OnboardingWindow
{
    private static string SampleFolderName => L.T("DropWheel 示例");
    private static string SampleName => L.T("示例照片.jpg");
    private readonly Window _window;
    private readonly SettingsStore _settings;
    private readonly DispatcherQueue _ui;
    private readonly TextBlock _press = Step();
    private readonly TextBlock _release = Step();
    private readonly InfoBar _outcome = new() { IsClosable = false };
    private bool _placed;

    public OnboardingWindow(SettingsStore settings, JobQueue queue, DispatcherQueue ui, Action showSettings)
    {
        _settings = settings;
        _ui = ui;
        _window = new Window { Title = L.T("DropWheel 入门") };
        _window.AppWindow.SetIcon("Assets/DropWheel.ico");
        _window.AppWindow.Closing += (window, e) =>
        {
            if (!Quitting)
            {
                e.Cancel = true;
                Finish("closed");
            }
        };

        AutomationProperties.SetAutomationId(_outcome, "outcome"); // for the acceptance driver
        var drag = Step();
        drag.Text = L.T("1. 在资源管理器里按住一个文件，开始拖动。");
        var tryIt = new Button { Content = L.T("试一试") };
        tryIt.Click += (_, _) => TrySample();
        var keys = new Button { Content = L.T("更改按键") };
        keys.Click += (_, _) => showSettings();
        var start = new Button { Content = L.T("开始使用"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        start.Click += (_, _) => Finish("started");
        Grid.SetColumn(start, 1);

        var page = new StackPanel { Spacing = 16, Padding = new Thickness(24, 20, 24, 24) };
        page.Children.Add(new TextBlock { Text = L.T("拖动文件，轻点一下，松手即转换"), FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        page.Children.Add(new StackPanel { Spacing = 8, Children = { drag, _press, _release } });
        page.Children.Add(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = L.T("手边没有合适的文件？「试一试」会在「图片\\{0}」里放一张示例照片并在资源管理器里选中它，用完可以删掉。", SampleFolderName), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
                tryIt,
            },
        });
        page.Children.Add(_outcome);
        page.Children.Add(new TextBlock
        {
            Text = L.T("关掉这个窗口后，DropWheel 留在任务栏右侧的通知区域里（新图标可能先收在 ^ 形的隐藏图标里），随时可以用。点它的图标打开任务窗口，那里能看到进度、撤销转换、打开设置。"),
            TextWrapping = TextWrapping.Wrap,
        });
        page.Children.Add(new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
            Children = { keys, start },
        });
        _window.Content = new ScrollViewer { Content = page };

        ShowSettings(settings.Current);
        settings.Changed += changed => _ui.TryEnqueue(() => ShowSettings(changed));
        // Read the state as the event fires: the job keeps changing, so a progress update queued
        // just before it finished would otherwise also read Done and report it twice.
        queue.JobChanged += job =>
        {
            if (job.State is JobState.Done or JobState.Failed)
            {
                _ui.TryEnqueue(() => ShowOutcome(job));
            }
        };
    }

    public HWND Handle => new(WinRT.Interop.WindowNative.GetWindowHandle(_window));

    /// <summary>Set before <see cref="Close"/> so the close is not turned into a hide.</summary>
    public bool Quitting { get; set; }

    /// <summary>Shows the guide, the first time centred on the primary work area.</summary>
    public void Show()
    {
        if (!_placed)
        {
            var area = DisplayArea.Primary.WorkArea;
            var scale = PInvoke.GetDpiForWindow(Handle) / 96.0;
            var (w, h) = ((int)(480 * scale), Math.Min((int)(600 * scale), area.Height - 48));
            _window.AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
            _placed = true;
        }

        FileLog.Info("onboarding: shown");
        _window.Activate();
    }

    public void Close()
    {
        Quitting = true;
        _window.Close();
    }

    private static TextBlock Step() => new() { TextWrapping = TextWrapping.Wrap, FontSize = 15 };

    private void ShowSettings(AppSettings settings)
    {
        var keys = settings.Keys.KeysFor(WheelMode.Convert);
        _press.Text = keys == Modifiers.None
            ? L.T("2. 还没有设置唤出转换轮盘的按键，先点「更改按键」选一个。")
            : L.T("2. 拖动途中轻点 {0}（按下马上松开），转换轮盘会在鼠标处弹出。", ModifierNames.Of(keys));
        var where = settings.Output.Location == OutputLocation.Folder && settings.Output.Folder.Length > 0
            ? L.T("「{0}」里", settings.Output.Folder)
            : L.T("源文件旁边");
        _release.Text = L.T("3. 移到想要的格式上松开鼠标。转换在后台进行，结果放在{0}。", where);
    }

    /// <summary>Writes the sample photo unless it is already there, then selects it in Explorer.</summary>
    private void TrySample()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), SampleFolderName);
        var path = Path.Combine(folder, SampleName);
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(folder);
                using var image = new MagickImage("radial-gradient:#FDBA74-#C2410C", new MagickReadSettings { Width = 1200, Height = 800 });
                image.Quality = 90;
                image.Write(path, MagickFormat.Jpeg);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MagickException)
        {
            FileLog.Error($"onboarding: could not write the sample {path}", ex);
            Tell(InfoBarSeverity.Error, L.T("示例照片没有放好"), ex.Message);
            return;
        }

        FileLog.Info($"onboarding: sample at {path}");
        if (Explorer.Select(path) is { } problem)
        {
            FileLog.Info($"onboarding: could not show the sample in Explorer: {problem}");
            Tell(InfoBarSeverity.Warning, L.T("没能打开资源管理器"), L.T("示例照片在 {0}，请自己打开那个文件夹。", path));
            return;
        }

        FileLog.Info("onboarding: showed the sample in its folder");
        Tell(InfoBarSeverity.Informational, L.T("示例照片已经选中"), L.T("现在把它拖起来，照上面的第 2、3 步做。"));
    }

    /// <summary>While the guide is open, says how each conversion ended: the task window is not on screen to say it.</summary>
    private void ShowOutcome(Job job)
    {
        if (!_window.AppWindow.IsVisible)
        {
            return;
        }

        switch (job.State)
        {
            case JobState.Done:
                FileLog.Info($"onboarding: saw {job.Label} done");
                Tell(InfoBarSeverity.Success, L.T("转换成功"), job.Outputs.Count > 0 ? L.T("{0}，结果在 {1}", job.Label, job.Outputs[0]) : job.Label);
                break;
            case JobState.Failed:
                FileLog.Info($"onboarding: saw {job.Label} fail");
                Tell(InfoBarSeverity.Error, L.T("没有转换成功"), job.Error ?? job.Label);
                break;
        }
    }

    private void Tell(InfoBarSeverity severity, string title, string message)
    {
        _outcome.Severity = severity;
        _outcome.Title = title;
        _outcome.Message = message;
        _outcome.IsOpen = true;
        AutomationProperties.SetName(_outcome, $"{title} {message}");
    }

    /// <summary>Marks the guide as seen and hides it.</summary>
    private void Finish(string how)
    {
        if (!_settings.Current.OnboardingDone)
        {
            try
            {
                _settings.Update(s => s with { OnboardingDone = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Seen is seen: the guide only comes back at the next launch.
                FileLog.Error("onboarding: could not save that the guide was seen", ex);
            }
        }

        FileLog.Info($"onboarding: done ({how})");
        _window.AppWindow.Hide();
    }
}
