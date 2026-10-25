using DropWheel.App.Diagnostics;
using DropWheel.Core.Jobs;
using DropWheel.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DropWheel.App.Queue;

/// <summary>
/// Task list: one row per job with its state, progress and what can be done with it (cancel, retry, show
/// the output in its folder, undo), and the way into Settings. Closing the window only hides it; the app
/// keeps running in the tray. Job events arrive on the queue's worker thread and are marshalled to the UI
/// thread here.
/// </summary>
internal sealed class QueueWindow
{
    private readonly Window _window;
    private readonly JobQueue _queue;
    private readonly DispatcherQueue _ui;
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _empty;
    private readonly Dictionary<Guid, Row> _byJob = [];
    private bool _placed;

    public QueueWindow(JobQueue queue, DispatcherQueue ui, Action showSettings)
    {
        _queue = queue;
        _ui = ui;
        _window = new Window { Title = "DropWheel 任务" };
        _window.AppWindow.SetIcon("Assets/DropWheel.ico");
        _window.AppWindow.Closing += (window, e) =>
        {
            if (!Quitting)
            {
                e.Cancel = true;
                window.Hide();
            }
        };
        _empty = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        _rows.Children.Add(_empty);
        var settings = new Button { Content = "设置", VerticalAlignment = VerticalAlignment.Center };
        settings.Click += (_, _) => showSettings();
        Grid.SetColumn(settings, 1);
        _window.Content = new Grid
        {
            Padding = new Thickness(16),
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = new GridLength(1, GridUnitType.Star) } },
            Children =
            {
                new Grid
                {
                    Margin = new Thickness(0, 0, 0, 12),
                    ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
                    Children = { new TextBlock { Text = "任务", FontSize = 20, FontWeight = FontWeights.SemiBold }, settings },
                },
                Rows(new ScrollViewer { Content = _rows }),
            },
        };
        queue.JobChanged += job =>
        {
            LogOutcome(job);
            _ui.TryEnqueue(() => Update(job));
        };
    }

    public HWND Handle => new(WinRT.Interop.WindowNative.GetWindowHandle(_window));

    /// <summary>Set before <see cref="Close"/> so the close is not turned into a hide.</summary>
    public bool Quitting { get; set; }

    /// <summary>Shows the window, the first time in the bottom-right of the primary work area.</summary>
    public void Show(bool activate = true)
    {
        if (!_placed)
        {
            var area = DisplayArea.Primary.WorkArea;
            var scale = PInvoke.GetDpiForWindow(Handle) / 96.0;
            var (w, h) = ((int)(440 * scale), (int)(380 * scale));
            _window.AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - w - 24, area.Y + area.Height - h - 24, w, h));
            _placed = true;
        }

        if (activate)
        {
            _window.Activate();
        }
        else
        {
            _window.AppWindow.Show(activateWindow: false);
        }
    }

    public void Close()
    {
        Quitting = true;
        _window.Close();
    }

    /// <summary>Names the keys that open the Convert wheel in the how-to line shown while the list is empty.</summary>
    public void ShowKeys(Modifiers keys) => _empty.Text = keys == Modifiers.None
        ? "还没有设置唤出转换轮盘的按键，请在「设置」里选择。"
        : $"从资源管理器拖动文件时轻点 {ModifierNames.Of(keys)}，轮盘会在鼠标处弹出；松手到扇区即开始转换。";

    private static void LogOutcome(Job job)
    {
        var took = job.FinishedAt is { } end ? $" in {(end - job.CreatedAt).TotalMilliseconds:F0} ms" : "";
        switch (job.State)
        {
            case JobState.Done:
                FileLog.Info($"queue: {job.Label} done{took}, outputs {string.Join(", ", job.Outputs)}");
                break;
            case JobState.Failed:
                FileLog.Info($"queue: {job.Label} failed{took}: {job.Error}");
                break;
            case JobState.Skipped:
                FileLog.Info($"queue: {job.Label} skipped: {job.Detail}");
                break;
            case JobState.Canceled:
                FileLog.Info($"queue: {job.Label} canceled{took}");
                break;
        }
    }

    private static ScrollViewer Rows(ScrollViewer viewer)
    {
        Grid.SetRow(viewer, 1);
        return viewer;
    }

    private void Update(Job job)
    {
        if (!_byJob.TryGetValue(job.Id, out var row))
        {
            row = new Row(job, this);
            _byJob[job.Id] = row;
            _rows.Children.Remove(_empty);
            _rows.Children.Insert(0, row.Element);
            if (!_window.AppWindow.IsVisible)
            {
                Show(activate: false);
            }
        }

        row.Refresh();
    }

    private void Cancel(Job job) => FileLog.Info(_queue.Cancel(job.Id)
        ? $"queue: canceling {job.Label}"
        : $"queue: cancel of {job.Label} refused in state {job.State}");

    private void Retry(Job job, Row row)
    {
        if (_queue.Retry(job.Id) is null)
        {
            FileLog.Info($"queue: retry of {job.Label} refused in state {job.State}");
            return;
        }

        // The new job's row takes this one's place at the top.
        FileLog.Info($"queue: retrying {job.Label}");
        _byJob.Remove(job.Id);
        _rows.Children.Remove(row.Element);
    }

    /// <summary>Opens the folder in Explorer with the first output selected.</summary>
    private static void Reveal(Job job, Row row)
    {
        var path = job.Outputs.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
        if (path is null)
        {
            FileLog.Info($"queue: outputs of {job.Label} are gone");
            row.Fail("输出文件已经不在原处了");
            row.Refresh();
            return;
        }

        FileLog.Info(Explorer.Select(path) is { } problem
            ? $"queue: could not show {path} in Explorer: {problem}"
            : $"queue: showed {path} in its folder");
    }

    private async Task UndoAsync(Job job, Row row)
    {
        row.Busy(true);
        try
        {
            if (!await _queue.UndoAsync(job.Id).ConfigureAwait(true))
            {
                FileLog.Info($"queue: undo of {job.Label} refused in state {job.State}");
            }
            else
            {
                FileLog.Info($"queue: undid {job.Label}, recycled {string.Join(", ", job.Outputs)}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Error($"queue: undo of {job.Label} failed", ex);
            row.Fail($"撤销失败：{ex.Message}");
        }
        finally
        {
            row.Busy(false);
            row.Refresh();
        }
    }

    /// <summary>
    /// A job's name, state and progress, and the buttons that apply to its state: 取消 while it waits or runs,
    /// 重试 once it failed or was canceled, 打开位置 and 撤销 once it is done. Each button's automation name
    /// carries the job's label so the rows can be told apart.
    /// </summary>
    private sealed class Row
    {
        private readonly Job _job;
        private readonly TextBlock _state = new() { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _progress = new() { Maximum = 1 };
        private readonly Button _cancel;
        private readonly Button _retry;
        private readonly Button _reveal;
        private readonly Button _undo;
        private string? _failure;

        public Row(Job job, QueueWindow owner)
        {
            _job = job;
            _cancel = Command("取消", () => owner.Cancel(job));
            _retry = Command("重试", () => owner.Retry(job, this));
            _reveal = Command("打开位置", () => Reveal(job, this));
            _undo = Command("撤销", async () => await owner.UndoAsync(job, this).ConfigureAwait(true));
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = job.Label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(_state);
            text.Children.Add(_progress);
            var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            commands.Children.Add(_cancel);
            commands.Children.Add(_retry);
            commands.Children.Add(_reveal);
            commands.Children.Add(_undo);
            Grid.SetColumn(commands, 1);
            Element = new Grid
            {
                ColumnSpacing = 12,
                ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
                Children = { text, commands },
            };
        }

        public Grid Element { get; }

        public void Busy(bool busy) => _undo.IsEnabled = !busy;

        public void Fail(string message) => _failure = message;

        public void Refresh()
        {
            var state = _job.State;
            _state.Text = _failure ?? state switch
            {
                JobState.Pending => "等待中",
                JobState.Running => _job.Progress > 0 ? $"转换中 {_job.Progress:P0}" : "转换中",
                JobState.Done => $"完成 → {string.Join("、", _job.Outputs.Select(Path.GetFileName))}",
                JobState.Failed => $"失败：{_job.Error}",
                JobState.Canceled => "已取消",
                JobState.Undone => "已撤销，输出已移到回收站",
                JobState.Skipped => _job.Detail ?? "已跳过",
                _ => state.ToString(),
            };

            // Engines that cannot tell how far they are report nothing until they finish.
            _progress.Visibility = Shown(state == JobState.Running);
            _progress.IsIndeterminate = _job.Progress <= 0;
            _progress.Value = _job.Progress;
            _cancel.Visibility = Shown(state is JobState.Pending or JobState.Running);
            _retry.Visibility = Shown(state is JobState.Failed or JobState.Canceled);
            _reveal.Visibility = Shown(state == JobState.Done && _failure is null);
            _undo.Visibility = Shown(state == JobState.Done);
        }

        private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

        private Button Command(string text, Action click)
        {
            var button = new Button { Content = text };
            AutomationProperties.SetName(button, $"{text} {_job.Label}");
            button.Click += (_, _) => click();
            return button;
        }
    }
}
