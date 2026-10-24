using DropWheel.App.Diagnostics;
using DropWheel.Core.Jobs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DropWheel.App.Queue;

/// <summary>
/// M0 task list: one row per job with its state and an Undo button once it is done. Closing the window
/// quits the app until there is a tray icon. Job events arrive on the queue's worker thread and are
/// marshalled to the UI thread here.
/// </summary>
internal sealed class QueueWindow
{
    private readonly Window _window;
    private readonly JobQueue _queue;
    private readonly DispatcherQueue _ui;
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _empty;
    private readonly Dictionary<Guid, Row> _byJob = [];

    public QueueWindow(JobQueue queue, DispatcherQueue ui)
    {
        _queue = queue;
        _ui = ui;
        _window = new Window { Title = "DropWheel 任务" };
        _window.AppWindow.SetIcon("Assets/DropWheel.ico");
        _empty = new TextBlock
        {
            Text = "从资源管理器拖动文件时轻点 Shift，轮盘会在鼠标处弹出；松手到扇区即开始转换。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        };
        _rows.Children.Add(_empty);
        _window.Content = new Grid
        {
            Padding = new Thickness(16),
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = new GridLength(1, GridUnitType.Star) } },
            Children =
            {
                new TextBlock { Text = "任务", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) },
                Rows(new ScrollViewer { Content = _rows }),
            },
        };
        queue.JobChanged += job =>
        {
            LogOutcome(job);
            _ui.TryEnqueue(() => Update(job));
        };
    }

    public event TypedEventHandler<object, WindowEventArgs>? Closed
    {
        add => _window.Closed += value;
        remove => _window.Closed -= value;
    }

    /// <summary>Shows the window in the bottom-right of the primary work area.</summary>
    public void Show()
    {
        var area = DisplayArea.Primary.WorkArea;
        var scale = PInvoke.GetDpiForWindow(new HWND(WinRT.Interop.WindowNative.GetWindowHandle(_window))) / 96.0;
        var (w, h) = ((int)(440 * scale), (int)(380 * scale));
        _window.AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - w - 24, area.Y + area.Height - h - 24, w, h));
        _window.Activate();
    }

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
            row = new Row(job, UndoAsync);
            _byJob[job.Id] = row;
            _rows.Children.Remove(_empty);
            _rows.Children.Insert(0, row.Element);
            _window.AppWindow.Show(false);
        }

        row.Refresh();
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

    private sealed class Row
    {
        private readonly Job _job;
        private readonly TextBlock _state = new() { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        private readonly Button _undo = new() { Content = "撤销" };
        private string? _failure;

        public Row(Job job, Func<Job, Row, Task> undo)
        {
            _job = job;
            AutomationProperties.SetName(_undo, $"撤销 {job.Label}");
            _undo.Click += async (_, _) => await undo(_job, this).ConfigureAwait(true);
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = job.Label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(_state);
            Grid.SetColumn(_undo, 1);
            _undo.VerticalAlignment = VerticalAlignment.Center;
            Element = new Grid
            {
                ColumnSpacing = 12,
                ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
                Children = { text, _undo },
            };
        }

        public Grid Element { get; }

        public void Busy(bool busy) => _undo.IsEnabled = !busy;

        public void Fail(string message) => _failure = message;

        public void Refresh()
        {
            _state.Text = _failure ?? _job.State switch
            {
                JobState.Pending => "等待中",
                JobState.Running => $"转换中 {_job.Progress:P0}",
                JobState.Done => $"完成 → {string.Join("、", _job.Outputs.Select(Path.GetFileName))}",
                JobState.Failed => $"失败：{_job.Error}",
                JobState.Canceled => "已取消",
                JobState.Undone => "已撤销，输出已移到回收站",
                JobState.Skipped => _job.Detail ?? "已跳过",
                _ => _job.State.ToString(),
            };
            _undo.Visibility = _job.State == JobState.Done ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
