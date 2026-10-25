using System.Diagnostics;
using DropWheel.App.Diagnostics;
using DropWheel.Core.Wheel;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DropWheel.App.Overlay;

/// <summary>
/// The wheel the user sees: a transparent, top-most, never-activated WinUI window with an acrylic disc
/// and six sectors drawn from <see cref="WheelGeometry"/>, the same numbers the drop target hit-tests
/// with. It never receives the drag itself; the Shell's DropTargetHost sits above it. Created once,
/// shown and hidden per drag. UI thread only.
/// </summary>
internal sealed class WheelWindow
{
    /// <summary>Room around the rim, in DIPs.</summary>
    private const double Margin = 10;
    private const double DiscPadding = 6;

    private static readonly Color Text = Color.FromArgb(255, 0xF2, 0xF2, 0xF2);
    private static readonly Color Text3 = Color.FromArgb(255, 0xA3, 0xA3, 0xA3);
    private static readonly Color Accent = Color.FromArgb(255, 0xFB, 0x92, 0x3C);
    private static readonly Color OnAccent = Color.FromArgb(255, 0x1A, 0x0D, 0x04);
    private static readonly Color SectorFill = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
    private static readonly Color SectorStroke = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly Path[] _sectors = new Path[WheelModel.SlotCount];
    private readonly TextBlock[] _labels = new TextBlock[WheelModel.SlotCount];
    private readonly TextBlock[] _subLabels = new TextBlock[WheelModel.SlotCount];
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly TextBlock _hint;
    private WheelModel? _model;
    private int _hovered = -1;
    private long _shownAt;

    public WheelWindow()
    {
        _window = new Window { Title = "DropWheel 轮盘" };
        _appWindow = _window.AppWindow;
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        _appWindow.SetPresenter(presenter);
        _appWindow.IsShownInSwitchers = false;

        // Never take focus from the drag source (spike finding: 65 of 65 shows left the foreground alone).
        var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        exStyle |= WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST;
        _ = PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)exStyle);
        _window.SystemBackdrop = new TransparentBackdrop();

        var canvas = new Canvas { Width = Size, Height = Size };
        var discSize = 2 * (WheelGeometry.OuterRadius + DiscPadding);
        var disc = new SystemBackdropElement
        {
            Width = discSize,
            Height = discSize,
            CornerRadius = new CornerRadius(discSize / 2),
            SystemBackdrop = new AcrylicBackdrop(),

            // Inside the Canvas a SystemBackdropElement renders nothing; centred in the root Grid it does.
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        for (var i = 0; i < WheelModel.SlotCount; i++)
        {
            _sectors[i] = new Path { Data = Wedge(i), StrokeThickness = 1, Stroke = new SolidColorBrush(SectorStroke) };
            canvas.Children.Add(_sectors[i]);

            _labels[i] = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            _subLabels[i] = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            var label = new StackPanel { Width = 84, Height = 44, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(_labels[i]);
            label.Children.Add(_subLabels[i]);
            var (x, y) = WheelGeometry.SectorCenter(i);
            Place(label, Center + x - 42, Center + y - 20);
            canvas.Children.Add(label);
        }

        _title = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Text),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 2 * WheelGeometry.InnerRadius - 14,
        };
        _subtitle = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Text3), TextAlignment = TextAlignment.Center };
        _hint = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Text3), TextAlignment = TextAlignment.Center };
        var hub = new StackPanel { Width = 2 * WheelGeometry.InnerRadius, Spacing = 2 };
        hub.Children.Add(_title);
        hub.Children.Add(_subtitle);
        hub.Children.Add(_hint);
        Place(hub, Center - WheelGeometry.InnerRadius, Center - 26);
        canvas.Children.Add(hub);

        _window.Content = new Grid { Children = { disc, canvas } };
        SetModel(null);
        SetHubHint(false);
    }

    /// <summary>Window width and height in DIPs.</summary>
    public static double Size => 2 * (WheelGeometry.OuterRadius + DiscPadding + Margin);

    private static double Center => Size / 2;

    /// <summary>
    /// Shows the wheel centred on a screen point (physical pixels) without activating it. Returns once
    /// the window is in the z-order, so the drop target host can be raised above it afterwards.
    /// </summary>
    public void ShowAt(int centerX, int centerY, double scale)
    {
        var px = (int)Math.Ceiling(Size * scale);
        _appWindow.MoveAndResize(new RectInt32(centerX - px / 2, centerY - px / 2, px, px));
        _shownAt = Stopwatch.GetTimestamp();
        CompositionTarget.Rendered += OnFirstFrame;
        _appWindow.Show(false);
    }

    public void Hide()
    {
        CompositionTarget.Rendered -= OnFirstFrame;
        _appWindow.Hide();
        SetModel(null);
    }

    public void Close() => _window.Close();

    /// <summary>
    /// Shows the window once off-screen and waits for its first frame, so the first real drag does not
    /// pay for XAML's first layout and the compositor's first surface (spike finding 4).
    /// </summary>
    public async Task PrewarmAsync()
    {
        var frame = new TaskCompletionSource();
        void OnRendered(object? sender, RenderedEventArgs e) => frame.TrySetResult();
        CompositionTarget.Rendered += OnRendered;
        var px = (int)Math.Ceiling(Size * 2);
        _appWindow.MoveAndResize(new RectInt32(-32000, -32000, px, px));
        _appWindow.Show(false);
        var started = Stopwatch.GetTimestamp();
        await Task.WhenAny(frame.Task, Task.Delay(2000)).ConfigureAwait(true);
        CompositionTarget.Rendered -= OnRendered;
        _appWindow.Hide();
        FileLog.Info($"overlay: prewarm {(frame.Task.IsCompleted ? "rendered" : "timed out")} after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");
    }

    /// <summary>The slots to show; null while the files are not known yet (between the tap and DragEnter).</summary>
    public void SetModel(WheelModel? model)
    {
        _model = model;
        _hovered = -1;
        _title.Text = model?.CenterTitle ?? "";
        _subtitle.Text = model?.CenterSubtitle ?? "读取文件…";
        for (var i = 0; i < WheelModel.SlotCount; i++)
        {
            var slot = model is not null && i < model.Slots.Count ? model.Slots[i] : null;
            _labels[i].Text = slot?.Label ?? "";
            _subLabels[i].Text = slot?.SubLabel ?? "";
            var name = slot is null ? $"扇区 {i + 1}（空）" : $"{slot.Label} {slot.SubLabel}";
            AutomationProperties.SetName(_sectors[i], name);
            Paint(i);
        }
    }

    /// <summary>
    /// True while the wheel opened off-centre and its sectors wait for the pointer to visit the hub
    /// (see <see cref="SectorGate"/>); the hub then says so instead of the usual Esc hint.
    /// </summary>
    public void SetHubHint(bool needed) => _hint.Text = needed ? "先移到圆心" : "Esc 取消";

    public void SetHovered(int sector)
    {
        var previous = _hovered;
        _hovered = sector;
        if (previous >= 0)
        {
            Paint(previous);
        }

        if (sector >= 0)
        {
            Paint(sector);
        }
    }

    private void Paint(int i)
    {
        var slot = _model is not null && i < _model.Slots.Count ? _model.Slots[i] : null;
        var enabled = slot?.Availability.IsAvailable == true;
        var hovered = enabled && i == _hovered;
        _sectors[i].Fill = new SolidColorBrush(hovered ? Accent : enabled ? SectorFill : Colors.Transparent);
        _labels[i].Foreground = new SolidColorBrush(hovered ? OnAccent : Text);
        _subLabels[i].Foreground = new SolidColorBrush(hovered ? OnAccent : Text3);
        _labels[i].Opacity = _subLabels[i].Opacity = enabled ? 1 : 0.45;
    }

    private void OnFirstFrame(object? sender, RenderedEventArgs e)
    {
        CompositionTarget.Rendered -= OnFirstFrame;
        FileLog.Info($"overlay: first frame {Stopwatch.GetElapsedTime(_shownAt).TotalMilliseconds:F0} ms after show");
    }

    private static PathGeometry Wedge(int index)
    {
        // A hair of gap between sectors so the stroke reads as a divider.
        const double GapDeg = 0.8;
        var a0 = WheelGeometry.StartAngle(index) + GapDeg;
        var a1 = WheelGeometry.StartAngle(index) + WheelGeometry.SectorSweep - GapDeg;
        var outer = WheelGeometry.OuterRadius;
        var inner = WheelGeometry.InnerRadius;
        var figure = new PathFigure { StartPoint = At(outer, a0), IsClosed = true };
        figure.Segments.Add(new ArcSegment { Point = At(outer, a1), Size = new Size(outer, outer), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = At(inner, a1) });
        figure.Segments.Add(new ArcSegment { Point = At(inner, a0), Size = new Size(inner, inner), SweepDirection = SweepDirection.Counterclockwise });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Point At(double radius, double degrees)
    {
        var a = degrees * Math.PI / 180;
        return new Point(Center + Math.Cos(a) * radius, Center + Math.Sin(a) * radius);
    }

    private static void Place(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
    }
}
