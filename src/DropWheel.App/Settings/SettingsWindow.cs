using DropWheel.App.Diagnostics;
using DropWheel.Core.Jobs;
using DropWheel.Core.Privacy;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using DropWheel.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DropWheel.App.Settings;

/// <summary>
/// The settings that change what the app does today: the keys that open each wheel and how quick a tap
/// must be, with a note when the wheel is solid because Windows has transparency effects off; which items
/// the Actions wheel offers, where results go, what they are called and what happens when the name is taken, and whether Windows starts it at sign-in; and what this computer keeps or sends:
/// the network requests counted since start, and the logs, which name the files converted, to open or
/// clear; and the language, which takes effect at the next start. Each change is saved as it is made,
/// the file name a moment after typing stops. Keys that <see cref="ConflictChecker"/> calls an error are
/// shown with the reason and not saved, so the Convert wheel is never left without a way to open it.
/// Closing only hides the window.
/// </summary>
/// <remarks>
/// Change handlers compare with <see cref="SettingsStore.Current"/> and save only a difference, because
/// WinUI raises some change events (TextChanged) after the code that set the value has returned, so a
/// "loading" flag cannot tell the user's edits from <see cref="Load"/>.
/// </remarks>
internal sealed class SettingsWindow
{
    private const string StartupTaskId = "DropWheelStartup"; // the TaskId in Package.appxmanifest
    private static readonly Modifiers[] KeyOrder = [Modifiers.Shift, Modifiers.Ctrl, Modifiers.Alt, Modifiers.Win];
    private readonly Window _window;
    private readonly SettingsStore _settings;
    private readonly Dictionary<(WheelMode Mode, Modifiers Key), CheckBox> _keys = [];
    private readonly Dictionary<FileAction, CheckBox> _actions = [];
    private readonly InfoBar _keyProblem = new() { IsClosable = false };
    private bool _altGr;
    private readonly NumberBox _tapWindow = new()
    {
        Header = L.T("轻点时限（毫秒）"),
        Description = L.T("按下到松开不超过这个时间，才算轻点。"),
        Minimum = 100,
        Maximum = 1000,
        SmallChange = 50,
        LargeChange = 100,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly ToggleSwitch _longPress = new() { Header = L.T("触屏或笔长按唤出转换轮盘"), OnContent = L.T("开"), OffContent = L.T("关") };
    private readonly TextBlock _longPressNote = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
    private readonly Windows.UI.ViewManagement.UISettings _system = new(); // kept: it raises AdvancedEffectsEnabledChanged only while alive
    private readonly InfoBar _solidWheel = new()
    {
        Severity = InfoBarSeverity.Informational,
        IsOpen = true,
        IsClosable = false,
        Title = L.T("轮盘是纯色的"),
        Message = L.T("Windows 的「透明效果」关着，轮盘用深灰底色代替半透明，用起来没有区别。想要半透明，在「个性化 › 颜色」里打开透明效果。"),
    };
    private readonly ToggleSwitch _edgeDock = new() { Header = L.T("拖到屏幕左右边缘时弹出停靠栏"), OnContent = L.T("开"), OffContent = L.T("关") };
    private readonly RadioButton _nextToSource = new() { Content = L.T("放在源文件旁边"), GroupName = "location" };
    private readonly RadioButton _toFolder = new() { Content = L.T("统一放到一个文件夹"), GroupName = "location" };
    private readonly TextBlock _folder = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _naming = new() { Header = L.T("文件名"), PlaceholderText = NamingTemplate.Default };
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly ComboBox _conflict = new() { Header = L.T("同名文件已经存在时"), Items = { L.T("自动编号，例如 photo (1).png"), L.T("跳过，不转换这个文件"), L.T("替换，旧的移到回收站"), L.T("每次询问") } };
    private readonly InfoBar _saveProblem = new() { Severity = InfoBarSeverity.Error, Title = L.T("设置没有保存") };
    private readonly ComboBox _language = new() { Items = { L.T("跟随 Windows"), "简体中文", "English" } };
    private readonly ToggleSwitch _startup = new() { Header = L.T("登录 Windows 时启动"), OnContent = L.T("开"), OffContent = L.T("关") };
    private readonly TextBlock _startupNote = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly StackPanel _startupSection;
    private readonly NetworkWatch _networkWatch;
    private readonly TextBlock _network = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _logNote = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
    private readonly DispatcherQueueTimer _namingPause;
    private StartupTask? _startupTask;

    /// <summary>With a package the startup task starts DropWheel at sign-in, without one <see cref="SignInRun"/>.</summary>
    private readonly bool _packaged = HasPackageIdentity();
    private bool _placed;
    private bool _loading;

    /// <param name="operations">The wheel items; those with a <see cref="IWheelOperation.Action"/> get a switch under 文件动作.</param>
    /// <param name="network">What 隐私与离线 counts; its count is shown live.</param>
    public SettingsWindow(SettingsStore settings, IReadOnlyList<IWheelOperation> operations, NetworkWatch network, DispatcherQueue ui)
    {
        _settings = settings;
        _networkWatch = network;
        network.Seen += _ => ui.TryEnqueue(ShowNetwork);
        _window = new Window { Title = L.T("DropWheel 设置") };
        _window.AppWindow.SetIcon("Assets/DropWheel.ico");
        _window.AppWindow.Closing += (window, e) =>
        {
            SaveNaming();
            if (!Quitting)
            {
                e.Cancel = true;
                window.Hide();
            }
        };

        var keys = new StackPanel { Spacing = 4 };
        foreach (var mode in WheelController.Modes)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new TextBlock { Text = WheelName(mode), Width = 48, VerticalAlignment = VerticalAlignment.Center });
            foreach (var key in KeyOrder)
            {
                var box = new CheckBox { Content = ModifierNames.Of(key), MinWidth = 88 };
                AutomationProperties.SetName(box, $"{WheelName(mode)} {ModifierNames.Of(key)}");
                box.Checked += (_, _) => KeysChanged(mode);
                box.Unchecked += (_, _) => KeysChanged(mode);
                _keys[(mode, key)] = box;
                row.Children.Add(box);
            }

            keys.Children.Add(row);
        }

        var actions = new StackPanel { Spacing = 4 };
        foreach (var op in operations.Where(o => o.Action is not null))
        {
            var action = op.Action!.Value;
            var box = new CheckBox { Content = L.T("{0}（{1}）", op.Label, op.Hint) };
            box.Checked += (_, _) => ActionChanged(action, true);
            box.Unchecked += (_, _) => ActionChanged(action, false);
            _actions[action] = box;
            actions.Children.Add(box);
        }

        var colors = new HyperlinkButton { Content = L.T("打开颜色设置") };
        colors.Click += async (_, _) =>
        {
            var opened = await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:colors"));
            FileLog.Info(opened ? "settings: opened Windows colour settings" : "settings: could not open Windows colour settings");
        };
        _solidWheel.ActionButton = colors;
        _system.AdvancedEffectsEnabledChanged += (_, _) => ui.TryEnqueue(ShowEffects);

        _tapWindow.ValueChanged += (_, e) => TapWindowChanged(e.NewValue);
        _longPress.Toggled += (_, _) =>
        {
            if (_longPress.IsOn != _settings.Current.Keys.TouchLongPress)
            {
                Save($"touch long press {(_longPress.IsOn ? "on" : "off")}", s => s with { Keys = s.Keys with { TouchLongPress = _longPress.IsOn } });
            }
        };
        _edgeDock.Toggled += (_, _) =>
        {
            if (_edgeDock.IsOn != _settings.Current.Keys.EdgeDock)
            {
                Save($"edge dock {(_edgeDock.IsOn ? "on" : "off")}", s => s with { Keys = s.Keys with { EdgeDock = _edgeDock.IsOn } });
            }
        };
        _nextToSource.Checked += (_, _) =>
        {
            if (_settings.Current.Output.Location != OutputLocation.NextToSource)
            {
                Save("output next to the source", s => s with { Output = s.Output with { Location = OutputLocation.NextToSource } });
            }
        };
        _toFolder.Checked += async (_, _) => await ToFolderAsync().ConfigureAwait(true);
        var pick = new Button { Content = L.T("选择文件夹…") };
        pick.Click += async (_, _) => await PickFolderAsync().ConfigureAwait(true);
        Grid.SetColumn(pick, 1);
        var folderRow = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
            Children = { _folder, pick },
        };

        AutomationProperties.SetAutomationId(_naming, "naming"); // for the acceptance driver
        AutomationProperties.SetAutomationId(_longPress, "touch-long-press");
        AutomationProperties.SetAutomationId(_edgeDock, "edge-dock");
        AutomationProperties.SetAutomationId(_solidWheel, "solid-wheel");
        AutomationProperties.SetAutomationId(_conflict, "conflict");
        AutomationProperties.SetAutomationId(_startup, "start-at-sign-in");
        AutomationProperties.SetAutomationId(_language, "language");
        AutomationProperties.SetName(_language, L.T("语言"));
        _namingPause = ui.CreateTimer();
        _namingPause.Interval = TimeSpan.FromMilliseconds(600);
        _namingPause.IsRepeating = false;
        _namingPause.Tick += (_, _) => SaveNaming();
        _naming.TextChanged += (_, _) =>
        {
            _preview.Text = Preview(_naming.Text);
            _namingPause.Stop();
            _namingPause.Start();
        };
        _conflict.SelectionChanged += (_, _) => ConflictChanged();
        _language.SelectionChanged += (_, _) => LanguageChanged();
        _startup.Toggled += async (_, _) => await StartupToggledAsync().ConfigureAwait(true);
        var openLogs = new Button { Content = L.T("打开日志文件夹") };
        openLogs.Click += (_, _) => OpenLogs();
        var clearLogs = new Button { Content = L.T("清除日志") };
        clearLogs.Click += async (_, _) => await ClearLogsAsync().ConfigureAwait(true);
        var logButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { openLogs, clearLogs } };

        var page = new StackPanel { Spacing = 24, Padding = new Thickness(20, 16, 20, 24) };
        page.Children.Add(new TextBlock { Text = L.T("设置"), FontSize = 20, FontWeight = FontWeights.SemiBold });
        page.Children.Add(_saveProblem);
        page.Children.Add(Section(L.T("唤出轮盘"), L.T("拖动文件时，把一行里选中的键一起按下再松开，那个轮盘就会在鼠标处弹出。转换轮盘把文件转成别的格式；工具轮盘压缩、缩小、拆分或提取音频；动作轮盘复制路径、重命名、压缩或删除。工具和动作轮盘一个键都不选就不会弹出。"), keys, _keyProblem, _tapWindow, _longPress, _longPressNote, _solidWheel));
        page.Children.Add(Section(L.T("停靠栏"), L.T("拖动文件时把鼠标推到屏幕左边或右边，那里会弹出停靠栏。文件先放进去，可以从几个文件夹陆续收集，回头一起或一个个拖走。放进去和拖出来都不会移动原文件，拖到文件夹里是复制。"), _edgeDock));
        page.Children.Add(Section(L.T("文件动作"), L.T("动作轮盘上哪几项能用。没勾选的仍然显示，只是灰色。删除默认不勾选，免得手一滑把文件扔进回收站。"), actions));
        page.Children.Add(Section(L.T("输出"), null,
            _nextToSource,
            _toFolder,
            folderRow,
            _naming,
            new TextBlock
            {
                Text = L.T("可以用：{name} 原文件名，{fmt} 目标格式，{date} 日期，{time} 时间，{n} 多个文件一起转换时的序号，{w} {h} 宽和高（部分格式）"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.75,
            },
            _preview,
            _conflict,
            new TextBlock
            {
                Text = L.T("替换时旧的文件或文件夹移到回收站，撤销任务不会把它移回来。拖进来的文件本身和这次刚生成的文件不会被替换，照样编号。"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.75,
            }));

        page.Children.Add(Section(L.T("隐私与离线"), L.T("读取、转换和写入都在这台电脑上完成，文件不会上传。DropWheel 没有账号，不收集使用数据，也不自己联网检查更新。"),
            _network, _logNote, logButtons));

        page.Children.Add(Section(L.T("语言"), L.T("跟随 Windows 时，Windows 的显示语言是中文就用简体中文，是其他语言就用英文。换了语言要退出 DropWheel 再打开才生效。"), _language));

        _startupSection = Section(L.T("启动"), null, _startup, _startupNote);
        page.Children.Add(_startupSection);
        _window.Content = new ScrollViewer { Content = page };
    }

    public HWND Handle => new(WinRT.Interop.WindowNative.GetWindowHandle(_window));

    /// <summary>Set before <see cref="Close"/> so the close is not turned into a hide.</summary>
    public bool Quitting { get; set; }

    /// <summary>Shows the window with the saved settings, the first time centred on the primary work area.</summary>
    public void Show()
    {
        Load();
        _ = LoadStartupAsync();
        if (!_placed)
        {
            var area = DisplayArea.Primary.WorkArea;
            var scale = PInvoke.GetDpiForWindow(Handle) / 96.0;
            var (w, h) = ((int)(480 * scale), Math.Min((int)(680 * scale), area.Height - 48));
            _window.AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
            _placed = true;
        }

        _window.Activate();
    }

    public void Close()
    {
        Quitting = true;
        _window.Close();
    }

    private static unsafe bool HasPackageIdentity()
    {
        uint length = 0;
        return PInvoke.GetCurrentPackageFullName(&length, default) != WIN32_ERROR.APPMODEL_ERROR_NO_PACKAGE;
    }

    private static string Preview(string template)
    {
        var name = new NamingTemplate(template).Expand(new NamingContext("photo", "png", "png", DateTimeOffset.Now, Sequence: 1, Width: 640, Height: 400));
        return L.T("例如 photo.jpg 转成 PNG：{0}.png", name);
    }

    private static StackPanel Section(string title, string? note, params UIElement[] children)
    {
        var section = new StackPanel { Spacing = 10 };
        section.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
        if (note is not null)
        {
            section.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        }

        foreach (var child in children)
        {
            section.Children.Add(child);
        }

        return section;
    }

    private static string WheelName(WheelMode mode) => mode switch
    {
        WheelMode.Convert => L.T("转换"),
        WheelMode.Tools => L.T("工具"),
        WheelMode.Actions => L.T("动作"),
        _ => mode.ToString(),
    };

    /// <summary>
    /// Ticking a row's boxes one by one passes through combinations that are not the saved ones; if the
    /// events come at once, <see cref="_loading"/> stops them being saved, if they come later, they match.
    /// </summary>
    private void Load()
    {
        var current = _settings.Current;
        _loading = true;
        foreach (var ((mode, key), box) in _keys)
        {
            box.IsChecked = (current.Keys.KeysFor(mode) & key) != 0;
        }

        foreach (var (action, box) in _actions)
        {
            box.IsChecked = current.Actions.IsEnabled(action);
        }

        _loading = false;

        _altGr = KeyboardLayouts.AnyHasAltGr(); // a layout added since the window last opened counts too
        FileLog.Info(_altGr ? "settings: a keyboard layout has AltGr, Ctrl+Alt gets a warning" : "settings: no keyboard layout has AltGr");
        ShowConflicts(ConflictChecker.Check(current.Keys, _altGr));
        _tapWindow.Value = current.Keys.TapWindowMs;
        _longPress.IsOn = current.Keys.TouchLongPress;
        _edgeDock.IsOn = current.Keys.EdgeDock;
        _longPressNote.Text = L.T("拖动时手指或笔停在原地 {0} 毫秒，转换轮盘就会弹出，鼠标停住不会。习惯停一下再抬起的话，轮盘会挡住要放下的地方，可以关掉。", current.Keys.LongPressMs);
        (current.Output.Location == OutputLocation.Folder ? _toFolder : _nextToSource).IsChecked = true;
        _folder.Text = current.Output.Folder.Length == 0 ? L.T("还没有选择文件夹") : current.Output.Folder;
        _naming.Text = current.Output.Naming;
        _preview.Text = Preview(current.Output.Naming);
        _conflict.SelectedIndex = current.Output.Conflict switch
        {
            ConflictPolicy.AutoNumber => 0,
            ConflictPolicy.Skip => 1,
            ConflictPolicy.OverwriteToRecycleBin => 2,
            ConflictPolicy.Ask => 3,
            _ => -1, // a number only a hand-edited file can hold; shown as unset
        };
        _language.SelectedIndex = current.Language switch
        {
            LanguageSetting.System => 0,
            LanguageSetting.Chinese => 1,
            LanguageSetting.English => 2,
            _ => -1,
        };
        ShowEffects();
        ShowNetwork();
        _logNote.Text = L.T("日志记下了转换过的文件路径，用来排查问题，只留最近 {0} 天，不会发给任何人。", LogRetention.KeepDays);
    }

    /// <summary>
    /// Shows the note while transparency effects are off, when acrylic falls back to its solid colour. The
    /// note is collapsed rather than closed, so that it leaves the automation tree as well.
    /// </summary>
    private void ShowEffects()
    {
        var on = _system.AdvancedEffectsEnabled;
        FileLog.Info(on ? "settings: transparency effects on" : "settings: transparency effects off, telling the wheel is solid");
        _solidWheel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowNetwork() => _network.Text = L.T("这次启动以来的网络请求：{0} 次", _networkWatch.Count);

    private void OpenLogs()
    {
        Directory.CreateDirectory(FileLog.Directory);
        var problem = Explorer.Select(File.Exists(FileLog.CurrentFile) ? FileLog.CurrentFile : FileLog.Directory);
        FileLog.Info(problem is null ? "settings: opened the log folder" : $"settings: could not open the log folder: {problem}");
    }

    private async Task ClearLogsAsync()
    {
        var deleted = await FileLog.ClearAsync().ConfigureAwait(true);
        FileLog.Info($"settings: cleared {deleted} log file(s)");
        _logNote.Text = L.T("已清除 {0} 个日志文件。", deleted);
    }

    private void KeysChanged(WheelMode mode)
    {
        if (_loading)
        {
            return;
        }

        var keys = KeyOrder.Where(k => _keys[(mode, k)].IsChecked == true).Aggregate(Modifiers.None, (all, k) => all | k);
        var conflicts = ConflictChecker.Check(_settings.Current.Keys.Rebound(mode, keys), _altGr);
        ShowConflicts(conflicts);
        if (keys == _settings.Current.Keys.KeysFor(mode))
        {
            return;
        }

        var what = $"{mode.ToString().ToLowerInvariant()} keys";
        if (conflicts.Any(c => c.Severity == ConflictSeverity.Error))
        {
            FileLog.Info($"settings: {what} '{ModifierNames.Of(keys)}' not saved: {string.Join(" ", conflicts.Select(c => c.Message))}");
            return;
        }

        Save($"{what} {ModifierNames.Of(keys)}", s => s with { Keys = s.Keys.Rebound(mode, keys) });
    }

    private void ActionChanged(FileAction action, bool on)
    {
        if (_loading || _settings.Current.Actions.IsEnabled(action) == on)
        {
            return;
        }

        Save($"file action {action} {(on ? "on" : "off")}", s => s with
        {
            Actions = new(on ? s.Actions.Enabled.Add(action) : s.Actions.Enabled.Remove(action)),
        });
    }

    private void ShowConflicts(IReadOnlyList<KeyConflict> conflicts)
    {
        _keyProblem.IsOpen = conflicts.Count > 0;
        if (conflicts.Count == 0)
        {
            return;
        }

        var error = conflicts.Any(c => c.Severity == ConflictSeverity.Error);
        _keyProblem.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        _keyProblem.Title = error ? L.T("这组按键没有保存") : "";
        _keyProblem.Message = string.Join(" ", conflicts.Select(c => c.Message).Distinct());
        AutomationProperties.SetName(_keyProblem, $"{_keyProblem.Title} {_keyProblem.Message}".Trim());
    }

    private void TapWindowChanged(double value)
    {
        if (double.IsNaN(value))
        {
            _tapWindow.Value = _settings.Current.Keys.TapWindowMs; // the box was cleared
            return;
        }

        var ms = (int)Math.Clamp(Math.Round(value), _tapWindow.Minimum, _tapWindow.Maximum);
        if (ms != _settings.Current.Keys.TapWindowMs)
        {
            Save($"tap window {ms} ms", s => s with { Keys = s.Keys with { TapWindowMs = ms } });
        }
    }

    private async Task ToFolderAsync()
    {
        var output = _settings.Current.Output;
        if (output.Location == OutputLocation.Folder)
        {
            return;
        }

        if (output.Folder.Length > 0)
        {
            Save($"output folder {output.Folder}", s => s with { Output = s.Output with { Location = OutputLocation.Folder } });
        }
        else if (!await PickFolderAsync().ConfigureAwait(true))
        {
            _nextToSource.IsChecked = true; // nowhere to put the files yet
        }
    }

    /// <returns>Whether a folder was chosen.</returns>
    private async Task<bool> PickFolderAsync()
    {
        string? path;
        try
        {
            path = (await new FolderPicker(_window.AppWindow.Id).PickSingleFolderAsync().AsTask().ConfigureAwait(true))?.Path;
        }
        catch (Exception ex)
        {
            FileLog.Error("settings: the folder picker failed", ex);
            return false;
        }

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        Save($"output folder {path}", s => s with { Output = s.Output with { Location = OutputLocation.Folder, Folder = path } });
        _folder.Text = path;
        _toFolder.IsChecked = true;
        return true;
    }

    private void SaveNaming()
    {
        _namingPause.Stop();
        var naming = _naming.Text.Trim() is { Length: > 0 } text ? text : NamingTemplate.Default;
        if (naming != _settings.Current.Output.Naming)
        {
            Save($"naming {naming}", s => s with { Output = s.Output with { Naming = naming } });
        }
    }

    private async Task LoadStartupAsync()
    {
        if (_startupSection.Visibility == Visibility.Collapsed)
        {
            return;
        }

        if (!_packaged)
        {
            ShowStartup(SignInRun.State);
            return;
        }

        try
        {
            _startupTask ??= await StartupTask.GetAsync(StartupTaskId).AsTask().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            FileLog.Error("settings: could not read the startup task", ex);
            _startupSection.Visibility = Visibility.Collapsed;
            return;
        }

        ShowStartup(_startupTask.State);
    }

    private void ShowStartup(StartupTaskState state)
    {
        _startup.IsOn = IsOn(state);
        _startup.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
        _startupNote.Text = state switch
        {
            StartupTaskState.DisabledByUser => L.T("已在任务管理器的「启动应用」或「设置 › 应用 › 启动」里关闭，只能在那里重新打开。"),
            StartupTaskState.DisabledByPolicy => L.T("已被组织的策略关闭。"),
            StartupTaskState.EnabledByPolicy => L.T("已由组织的策略打开。"),
            _ => L.T("开机后只在托盘里待命，不打开窗口。"),
        };
    }

    private static bool IsOn(StartupTaskState state) => state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

    private async Task StartupToggledAsync()
    {
        if (!_packaged)
        {
            StartupToggled();
            return;
        }

        if (_startupTask is not { } task || _startup.IsOn == IsOn(task.State))
        {
            return;
        }

        try
        {
            if (_startup.IsOn)
            {
                // A packaged desktop app gets no consent dialog; the state says whether the user had turned it off.
                FileLog.Info($"settings: startup task {await task.RequestEnableAsync().AsTask().ConfigureAwait(true)}");
            }
            else
            {
                task.Disable();
                FileLog.Info("settings: startup task Disabled");
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("settings: could not change the startup task", ex);
        }

        ShowStartup(task.State);
    }

    /// <summary>Without a package: the Run value instead of the startup task.</summary>
    private void StartupToggled()
    {
        if (_startup.IsOn == IsOn(SignInRun.State))
        {
            return;
        }

        try
        {
            SignInRun.Set(_startup.IsOn);
            FileLog.Info($"settings: sign-in run value {(_startup.IsOn ? "set" : "removed")}");
        }
        catch (Exception ex)
        {
            FileLog.Error("settings: could not change the sign-in run value", ex);
        }

        ShowStartup(SignInRun.State);
    }

    private void ConflictChanged()
    {
        var policy = _conflict.SelectedIndex switch
        {
            0 => ConflictPolicy.AutoNumber,
            1 => ConflictPolicy.Skip,
            2 => ConflictPolicy.OverwriteToRecycleBin,
            3 => ConflictPolicy.Ask,
            _ => (ConflictPolicy?)null,
        };
        if (policy is { } p && p != _settings.Current.Output.Conflict)
        {
            Save($"conflict {p}", s => s with { Output = s.Output with { Conflict = p } });
        }
    }

    /// <summary>Saved for the next start; the windows already built keep the language they were built in.</summary>
    private void LanguageChanged()
    {
        var language = _language.SelectedIndex switch
        {
            0 => LanguageSetting.System,
            1 => LanguageSetting.Chinese,
            2 => LanguageSetting.English,
            _ => (LanguageSetting?)null,
        };
        if (language is { } l && l != _settings.Current.Language)
        {
            Save($"language {l}", s => s with { Language = l });
        }
    }

    private void Save(string what, Func<AppSettings, AppSettings> change)
    {
        try
        {
            _settings.Update(change);
            _saveProblem.IsOpen = false;
            FileLog.Info($"settings: saved {what}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Error($"settings: could not save {what}", ex);
            _saveProblem.Message = ex.Message;
            _saveProblem.IsOpen = true;
        }
    }
}
