using DropWheel.App.Diagnostics;
using DropWheel.Core.Jobs;
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
/// must be, which items the Actions wheel offers, where results go, what they are called and what happens
/// when the name is taken, and whether Windows starts it at sign-in. Each change is saved as it is made,
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
        Header = "轻点时限（毫秒）",
        Description = "按下到松开不超过这个时间，才算轻点。",
        Minimum = 100,
        Maximum = 1000,
        SmallChange = 50,
        LargeChange = 100,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly ToggleSwitch _longPress = new() { Header = "触屏或笔长按唤出转换轮盘", OnContent = "开", OffContent = "关" };
    private readonly TextBlock _longPressNote = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
    private readonly RadioButton _nextToSource = new() { Content = "放在源文件旁边", GroupName = "location" };
    private readonly RadioButton _toFolder = new() { Content = "统一放到一个文件夹", GroupName = "location" };
    private readonly TextBlock _folder = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _naming = new() { Header = "文件名", PlaceholderText = NamingTemplate.Default };
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly ComboBox _conflict = new() { Header = "同名文件已经存在时", Items = { "自动编号，例如 photo (1).png", "跳过，不转换这个文件" } };
    private readonly InfoBar _saveProblem = new() { Severity = InfoBarSeverity.Error, Title = "设置没有保存" };
    private readonly ToggleSwitch _startup = new() { Header = "登录 Windows 时启动", OnContent = "开", OffContent = "关" };
    private readonly TextBlock _startupNote = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly StackPanel _startupSection;
    private readonly DispatcherQueueTimer _namingPause;
    private StartupTask? _startupTask;
    private bool _placed;
    private bool _loading;

    /// <param name="operations">The wheel items; those with a <see cref="IWheelOperation.Action"/> get a switch under 文件动作.</param>
    public SettingsWindow(SettingsStore settings, IReadOnlyList<IWheelOperation> operations, DispatcherQueue ui)
    {
        _settings = settings;
        _window = new Window { Title = "DropWheel 设置" };
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
            var box = new CheckBox { Content = $"{op.Label}（{op.Hint}）" };
            box.Checked += (_, _) => ActionChanged(action, true);
            box.Unchecked += (_, _) => ActionChanged(action, false);
            _actions[action] = box;
            actions.Children.Add(box);
        }

        _tapWindow.ValueChanged += (_, e) => TapWindowChanged(e.NewValue);
        _longPress.Toggled += (_, _) =>
        {
            if (_longPress.IsOn != _settings.Current.Keys.TouchLongPress)
            {
                Save($"touch long press {(_longPress.IsOn ? "on" : "off")}", s => s with { Keys = s.Keys with { TouchLongPress = _longPress.IsOn } });
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
        var pick = new Button { Content = "选择文件夹…" };
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
        _startup.Toggled += async (_, _) => await StartupToggledAsync().ConfigureAwait(true);

        var page = new StackPanel { Spacing = 24, Padding = new Thickness(20, 16, 20, 24) };
        page.Children.Add(new TextBlock { Text = "设置", FontSize = 20, FontWeight = FontWeights.SemiBold });
        page.Children.Add(_saveProblem);
        page.Children.Add(Section("唤出轮盘", "拖动文件时，把一行里选中的键一起按下再松开，那个轮盘就会在鼠标处弹出。转换轮盘把文件转成别的格式；工具轮盘压缩、缩小、拆分或提取音频；动作轮盘复制路径、重命名、压缩或删除。工具和动作轮盘一个键都不选就不会弹出。", keys, _keyProblem, _tapWindow, _longPress, _longPressNote));
        page.Children.Add(Section("文件动作", "动作轮盘上哪几项能用。没勾选的仍然显示，只是灰色。删除默认不勾选，免得手一滑把文件扔进回收站。", actions));
        page.Children.Add(Section("输出", null,
            _nextToSource,
            _toFolder,
            folderRow,
            _naming,
            new TextBlock
            {
                Text = "可以用：{name} 原文件名，{fmt} 目标格式，{date} 日期，{time} 时间，{n} 多个文件一起转换时的序号，{w} {h} 宽和高（部分格式）",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.75,
            },
            _preview,
            _conflict));

        // Unpackaged builds (development, the acceptance run) have no startup task to show.
        _startupSection = Section("启动", null, _startup, _startupNote);
        _startupSection.Visibility = HasPackageIdentity() ? Visibility.Visible : Visibility.Collapsed;
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
        return $"例如 photo.jpg 转成 PNG：{name}.png";
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
        WheelMode.Convert => "转换",
        WheelMode.Tools => "工具",
        WheelMode.Actions => "动作",
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
        _longPressNote.Text = $"拖动时手指或笔停在原地 {current.Keys.LongPressMs} 毫秒，转换轮盘就会弹出，鼠标停住不会。习惯停一下再抬起的话，轮盘会挡住要放下的地方，可以关掉。";
        (current.Output.Location == OutputLocation.Folder ? _toFolder : _nextToSource).IsChecked = true;
        _folder.Text = current.Output.Folder.Length == 0 ? "还没有选择文件夹" : current.Output.Folder;
        _naming.Text = current.Output.Naming;
        _preview.Text = Preview(current.Output.Naming);
        _conflict.SelectedIndex = current.Output.Conflict switch
        {
            ConflictPolicy.AutoNumber => 0,
            ConflictPolicy.Skip => 1,
            _ => -1, // a policy only a hand-edited file can hold; shown as unset
        };
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
        _keyProblem.Title = error ? "这组按键没有保存" : "";
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
            StartupTaskState.DisabledByUser => "已在任务管理器的「启动应用」或「设置 › 应用 › 启动」里关闭，只能在那里重新打开。",
            StartupTaskState.DisabledByPolicy => "已被组织的策略关闭。",
            StartupTaskState.EnabledByPolicy => "已由组织的策略打开。",
            _ => "开机后只在托盘里待命，不打开窗口。",
        };
    }

    private static bool IsOn(StartupTaskState state) => state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

    private async Task StartupToggledAsync()
    {
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

    private void ConflictChanged()
    {
        var policy = _conflict.SelectedIndex switch
        {
            0 => ConflictPolicy.AutoNumber,
            1 => ConflictPolicy.Skip,
            _ => (ConflictPolicy?)null,
        };
        if (policy is { } p && p != _settings.Current.Output.Conflict)
        {
            Save($"conflict {p}", s => s with { Output = s.Output with { Conflict = p } });
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
