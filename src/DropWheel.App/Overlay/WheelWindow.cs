using System.Diagnostics;
using System.Numerics;
using DropWheel.App.Diagnostics;
using DropWheel.Core.Wheel;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using CompositionTarget = Microsoft.UI.Xaml.Media.CompositionTarget;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DropWheel.App.Overlay;

/// <summary>
/// The wheel the user sees: a transparent, top-most, never-activated WinUI window with an acrylic disc
/// and six sectors drawn from <see cref="WheelGeometry"/>, the same numbers the drop target hit-tests
/// with, and, when a group sector opens, its items on a ring around the disc (<see cref="SetRing"/>).
/// It never receives the drag itself; the Shell's DropTargetHost sits above it. Created once,
/// shown and hidden per drag. After the first frame of a show it checks, now and then, that the screen
/// shows the wheel and not a flat white block (<see cref="FrameCheck"/>). UI thread only.
/// </summary>
/// <remarks>
/// Motion runs on the compositor (see docs/design/README.md): the wheel scales in from .92 over
/// <c>DwMotionWheelIn</c>, fades out over <c>DwMotionSelect</c>, a hovered sector lifts to 1.08, and ring items
/// fan out 20 ms apart. With system animations off (<see cref="UISettings.AnimationsEnabled"/>) every one of
/// those is a plain property set: nothing moves, nothing is delayed.
/// </remarks>
internal sealed class WheelWindow
{
    /// <summary>How much a hovered sector or ring item grows.</summary>
    private const float Lift = 1.08f;

    /// <summary>Ring items appear one after another, this far apart.</summary>
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(20);

    /// <summary>Room around the rim, in DIPs.</summary>
    private const double Margin = 10;
    private const double DiscPadding = 6;

    /// <summary>A sector's label block: icon, label, sub-label, centred on the sector; the band is 88 DIPs deep.</summary>
    private const double SectorLabelWidth = 84;
    private const double SectorLabelHeight = 70;

    /// <summary>A ring item's label block; the ring band is 80 DIPs deep.</summary>
    private const double ChildLabelWidth = 80;
    private const double ChildLabelHeight = 60;

    /// <summary>From the first frame to the screen copy: Rendered fires before DWM presents the frame.</summary>
    private const int FrameCheckDelayMs = 100;

    /// <summary>
    /// From a copy that looks flat to the second look. A wheel that has just moved can reach the screen later
    /// than that: on the VM the first show after start once copied the Explorer window under it (#F9FBFC on
    /// 98%) while the next show copied the disc.
    /// </summary>
    private const int FrameRecheckMs = 500;

    private static readonly Color Text = Color.FromArgb(255, 0xF2, 0xF2, 0xF2);
    private static readonly Color Text3 = Color.FromArgb(255, 0xA3, 0xA3, 0xA3);
    private static readonly Color Accent = Color.FromArgb(255, 0xFB, 0x92, 0x3C);
    private static readonly Color OnAccent = Color.FromArgb(255, 0x1A, 0x0D, 0x04);
    private static readonly Color SectorFill = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
    private static readonly Color SectorStroke = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
    private static readonly Color SectorOpen = Color.FromArgb(0x59, 0xFB, 0x92, 0x3C);

    /// <summary>The ring has no acrylic of its own: a dark, nearly solid fill keeps its text readable over any desktop.</summary>
    private static readonly Color ChildFill = Color.FromArgb(0xE0, 0x20, 0x20, 0x20);
    private static readonly Color ChildOff = Color.FromArgb(0xA0, 0x20, 0x20, 0x20);

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly Path[] _sectors = new Path[WheelModel.SlotCount];
    private readonly TextBlock[] _glyphs = new TextBlock[WheelModel.SlotCount];
    private readonly TextBlock[] _labels = new TextBlock[WheelModel.SlotCount];
    private readonly TextBlock[] _subLabels = new TextBlock[WheelModel.SlotCount];
    private readonly Path[] _children = new Path[LayoutGroup.Capacity];
    private readonly Grid[] _childPanels = new Grid[LayoutGroup.Capacity];
    private readonly TextBlock[] _childGlyphs = new TextBlock[LayoutGroup.Capacity];
    private readonly TextBlock[] _childLabels = new TextBlock[LayoutGroup.Capacity];
    private readonly TextBlock[] _childSubLabels = new TextBlock[LayoutGroup.Capacity];

    // One brush per thing painted, recoloured in place: a paint allocates nothing, and the hover motion can interpolate it.
    private readonly SolidColorBrush[] _fills = new SolidColorBrush[WheelModel.SlotCount];
    private readonly SolidColorBrush[] _inks = new SolidColorBrush[WheelModel.SlotCount];
    private readonly SolidColorBrush[] _subInks = new SolidColorBrush[WheelModel.SlotCount];
    private readonly SolidColorBrush[] _childFills = new SolidColorBrush[LayoutGroup.Capacity];
    private readonly SolidColorBrush[] _childInks = new SolidColorBrush[LayoutGroup.Capacity];
    private readonly SolidColorBrush[] _childSubInks = new SolidColorBrush[LayoutGroup.Capacity];
    private readonly Canvas _canvas;
    private readonly UISettings _system = new();
    private readonly Visual _root;
    private readonly Compositor _compositor;
    private readonly CompositionEasingFunction _ease;
    private readonly TimeSpan _wheelIn = Motion("DwMotionWheelIn", 150);
    private readonly TimeSpan _select = Motion("DwMotionSelect", 100);
    private readonly Visual[] _sectorVisuals = new Visual[WheelModel.SlotCount];
    private readonly Visual[] _childVisuals = new Visual[LayoutGroup.Capacity];
    private readonly Visual[] _childPanelVisuals = new Visual[LayoutGroup.Capacity];
    private bool _shown;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly TextBlock _hint;
    private readonly FrameWatch _frames = new(TimeProvider.System);
    private WheelModel? _model;
    private bool _hubFirst;
    private WheelHit _hovered = WheelHit.None;
    private int _ringParent = -1;
    private IReadOnlyList<WheelSlot>? _ringChildren;
    private long _shownAt;
    private RectInt32 _bounds;
    private int _generation; // moves on at every show, hide and close, so a frame check knows the wheel stayed put

    /// <param name="white">Diagnostic (<c>--white-wheel</c>): an opaque white layer over the whole window, as a stuck graphics driver shows it.</param>
    public WheelWindow(bool white = false)
    {
        _window = new Window { Title = L.T("DropWheel 轮盘") };
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

        // The canvas is always the ring's size and is centred in the window, which is the ring's size only for
        // a wheel with groups; a Canvas never clips its children, so the sectors stay whole either way.
        var canvas = _canvas = new Canvas { Width = RingSize, Height = RingSize };
        var frame = new Canvas { Children = { canvas } };
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

        var symbols = SymbolFont();
        for (var i = 0; i < WheelModel.SlotCount; i++)
        {
            _fills[i] = new SolidColorBrush(Colors.Transparent);
            _inks[i] = new SolidColorBrush(Text);
            _subInks[i] = new SolidColorBrush(Text3);
            _sectors[i] = new Path { Data = Wedge(i), StrokeThickness = 1, Stroke = new SolidColorBrush(SectorStroke), Fill = _fills[i] };
            AutomationProperties.SetAutomationId(_sectors[i], $"Sector{i}");
            canvas.Children.Add(_sectors[i]);

            _glyphs[i] = new TextBlock { FontFamily = symbols, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, Foreground = _inks[i], Margin = new Thickness(0, 0, 0, 2) };
            _labels[i] = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = _inks[i] };
            _subLabels[i] = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = _subInks[i] };
            var (x, y) = WheelGeometry.SectorCenter(i);
            var label = LabelPanel(_glyphs[i], _labels[i], _subLabels[i], SectorLabelWidth, SectorLabelHeight, Center + x, Center + y);
            Canvas.SetZIndex(label, 2); // above a lifted neighbour
            canvas.Children.Add(label);
            _sectorVisuals[i] = ElementCompositionPreview.GetElementVisual(_sectors[i]);
            _sectorVisuals[i].CenterPoint = new Vector3((float)(Center + x), (float)(Center + y), 0);
        }

        for (var i = 0; i < LayoutGroup.Capacity; i++)
        {
            _childFills[i] = new SolidColorBrush(ChildOff);
            _childInks[i] = new SolidColorBrush(Text);
            _childSubInks[i] = new SolidColorBrush(Text3);
            _children[i] = new Path { StrokeThickness = 1, Stroke = new SolidColorBrush(SectorStroke), Fill = _childFills[i], Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(_children[i], $"Child{i}");
            canvas.Children.Add(_children[i]);

            _childGlyphs[i] = new TextBlock { FontFamily = symbols, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, Foreground = _childInks[i], Margin = new Thickness(0, 0, 0, 1) };
            _childLabels[i] = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = _childInks[i] };
            _childSubLabels[i] = new TextBlock { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = _childSubInks[i] };
            var panel = _childPanels[i] = LabelPanel(_childGlyphs[i], _childLabels[i], _childSubLabels[i], ChildLabelWidth, ChildLabelHeight, 0, 0);
            panel.Visibility = Visibility.Collapsed;
            Canvas.SetZIndex(panel, 2);
            canvas.Children.Add(panel);
            _childVisuals[i] = ElementCompositionPreview.GetElementVisual(_children[i]);
            _childPanelVisuals[i] = ElementCompositionPreview.GetElementVisual(panel);
            _childPanelVisuals[i].CenterPoint = new Vector3((float)(ChildLabelWidth / 2), (float)(ChildLabelHeight / 2), 0);
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
        _subtitle = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Text3),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 3,
            MaxWidth = 2 * WheelGeometry.InnerRadius - 18,
        };
        _hint = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Text3), TextAlignment = TextAlignment.Center };
        AutomationProperties.SetAutomationId(_title, "WheelTitle");
        AutomationProperties.SetAutomationId(_subtitle, "WheelSubtitle");
        var hub = new StackPanel { Width = 2 * WheelGeometry.InnerRadius, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        hub.Children.Add(_title);
        hub.Children.Add(_subtitle);
        hub.Children.Add(_hint);

        // Centred in the hole, so a reason that wraps grows both ways.
        var hole = new Grid { Width = 2 * WheelGeometry.InnerRadius, Height = 2 * WheelGeometry.InnerRadius, IsHitTestVisible = false, Children = { hub } };
        Place(hole, Center - WheelGeometry.InnerRadius, Center - WheelGeometry.InnerRadius);
        canvas.Children.Add(hole);

        var content = new Grid { Children = { disc, frame } };
        _window.Content = content;
        if (white)
        {
            content.Children.Add(new Border { Background = new SolidColorBrush(Colors.White) });
        }

        _root = ElementCompositionPreview.GetElementVisual(content);
        _compositor = _root.Compositor;
        _ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        SetModel(null);
        SetHubHint(false);
    }

    /// <summary>Window width and height in DIPs for a wheel without groups: the disc and a margin.</summary>
    public static double Size => 2 * (WheelGeometry.OuterRadius + DiscPadding + Margin);

    /// <summary>Window width and height in DIPs for a wheel with groups: room for the ring around the disc.</summary>
    public static double RingSize => 2 * (WheelGeometry.RingOuterRadius + WheelGeometry.RingMargin);

    public static double SizeFor(bool ring) => ring ? RingSize : Size;

    private static double Center => RingSize / 2;

    /// <summary>Raised when a frame check finds a flat white block, once until a later check finds the wheel drawn.</summary>
    public event Action? LooksFlat;

    /// <summary>
    /// Shows the wheel centred on a screen point (physical pixels) without activating it, sized for the
    /// ring when <paramref name="ring"/> says the wheel has groups. Returns once the window is in the
    /// z-order, so the drop target host can be raised above it afterwards.
    /// </summary>
    public void ShowAt(int centerX, int centerY, double scale, bool ring = false)
    {
        var dip = Fit(centerX, centerY, scale, ring);
        _generation++;
        _shownAt = Stopwatch.GetTimestamp();
        CompositionTarget.Rendered += OnFirstFrame;

        // A fade still running from the last close is cut short: this show owns the window now.
        _root.StopAnimation("Opacity");
        _root.StopAnimation("Scale");
        var animated = Animated;
        if (animated)
        {
            _root.CenterPoint = new Vector3((float)(dip / 2), (float)(dip / 2), 0);
            var grow = _compositor.CreateVector3KeyFrameAnimation();
            grow.InsertKeyFrame(0f, new Vector3(0.92f, 0.92f, 1f));
            grow.InsertKeyFrame(1f, Vector3.One, _ease);
            grow.Duration = _wheelIn;
            var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
            fadeIn.InsertKeyFrame(0f, 0f);
            fadeIn.InsertKeyFrame(1f, 1f, _ease);
            fadeIn.Duration = _wheelIn;
            _root.StartAnimation("Scale", grow);
            _root.StartAnimation("Opacity", fadeIn);
        }
        else
        {
            _root.Scale = Vector3.One;
            _root.Opacity = 1;
        }

        _shown = true;
        _appWindow.Show(false);
        FileLog.Info(animated ? $"overlay: motion on, wheel in {_wheelIn.TotalMilliseconds:F0} ms" : "overlay: motion off, system animations are disabled");
    }

    /// <summary>
    /// Moves and resizes the shown window for another wheel at the same centre (the user tapped another
    /// wheel's keys mid-drag). The frame check in flight is dropped; nothing is shown or hidden.
    /// </summary>
    public void Resize(int centerX, int centerY, double scale, bool ring)
    {
        Fit(centerX, centerY, scale, ring);
        _generation++;
    }

    /// <summary>
    /// Fades the wheel out over <c>DwMotionSelect</c> and hides it; at once when animations are off. A show
    /// that comes first takes the window back (see <see cref="ShowAt"/>) and the hide is dropped.
    /// </summary>
    public void Hide()
    {
        CompositionTarget.Rendered -= OnFirstFrame;
        _generation++;
        if (!_shown || !Animated)
        {
            HideNow();
            return;
        }

        var generation = _generation;
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = _select;
        _root.StartAnimation("Opacity", fade);
        batch.Completed += (_, _) =>
        {
            if (generation == _generation)
            {
                HideNow();
            }
        };
        batch.End();
    }

    private void HideNow()
    {
        _shown = false;
        _appWindow.Hide();
        _root.StopAnimation("Opacity");
        _root.Opacity = 1;
        _root.Scale = Vector3.One;
        for (var i = 0; i < WheelModel.SlotCount; i++)
        {
            Settle(_sectors[i], _sectorVisuals[i]);
        }

        SetRing(-1, null);
        SetModel(null);
    }

    private bool Animated => _system.AnimationsEnabled;

    /// <summary>Places the window for the wheel's footprint and returns that footprint in DIPs.</summary>
    private double Fit(int centerX, int centerY, double scale, bool ring)
    {
        var dip = SizeFor(ring);
        var px = (int)Math.Ceiling(dip * scale);
        var discPx = (int)Math.Ceiling(Size * scale);
        Place(_canvas, (dip - RingSize) / 2, (dip - RingSize) / 2);
        _appWindow.MoveAndResize(new RectInt32(centerX - px / 2, centerY - px / 2, px, px));

        // The frame check looks at the disc only: around it the window is transparent and shows the desktop.
        _bounds = new RectInt32(centerX - discPx / 2, centerY - discPx / 2, discPx, discPx);
        return dip;
    }

    public void Close()
    {
        _generation++;
        _window.Close();
    }

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

    /// <summary>
    /// The slots to show; null while the files are not known yet (between the tap and DragEnter). A search
    /// replaces the model under a pointer that has not moved, so the hovered sector stays lit.
    /// </summary>
    public void SetModel(WheelModel? model)
    {
        _model = model;
        if (model is null)
        {
            _hovered = WheelHit.None;
        }

        ShowHub();
        for (var i = 0; i < WheelModel.SlotCount; i++)
        {
            var slot = Slot(i);
            SetGlyph(_glyphs[i], slot?.Glyph);
            _labels[i].Text = slot?.Label ?? "";
            _subLabels[i].Text = slot?.SubLabel ?? "";
            AutomationProperties.SetName(_sectors[i], slot?.AccessibleName ?? L.T("扇区 {0}（空）", i + 1));
            Paint(i);
        }
    }

    /// <summary>
    /// True while the wheel opened off-centre and its sectors wait for the pointer to visit the hub
    /// (see <see cref="SectorGate"/>); the hub then says so instead of the usual Esc hint.
    /// </summary>
    public void SetHubHint(bool needed)
    {
        _hubFirst = needed;
        ShowHub();
    }

    /// <summary>
    /// The pointer moved onto a sector or a ring item. Only an enabled one lights up, but any with
    /// something to say (see <see cref="WheelSlot.Detail"/>) says it in the hub.
    /// </summary>
    public void SetHovered(WheelHit hit)
    {
        var previous = _hovered;
        _hovered = hit;
        ShowHub();
        Repaint(previous);
        Repaint(hit);
        if (previous != hit)
        {
            Raise(previous, false);
            Raise(hit, true);
        }
    }

    /// <summary>
    /// Opens the ring on sector <paramref name="parent"/> with the group's items, or closes it with -1 and null.
    /// The items sit on the ring centred on the sector, in the group's order.
    /// </summary>
    public void SetRing(int parent, IReadOnlyList<WheelSlot>? children)
    {
        var previous = _ringParent;
        _ringParent = children is { Count: > 0 } ? parent : -1;
        _ringChildren = _ringParent >= 0 ? children : null;
        var count = _ringChildren?.Count ?? 0;
        for (var i = 0; i < LayoutGroup.Capacity; i++)
        {
            var shown = i < count;
            _children[i].Visibility = _childPanels[i].Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (!shown)
            {
                continue;
            }

            var child = _ringChildren![i];
            _children[i].Data = RingWedge(_ringParent, count, i);
            SetGlyph(_childGlyphs[i], child.Glyph);
            _childLabels[i].Text = child.Label;
            _childSubLabels[i].Text = child.SubLabel ?? "";
            AutomationProperties.SetName(_children[i], child.AccessibleName);
            var (x, y) = WheelGeometry.ChildCenter(_ringParent, count, i);
            Place(_childPanels[i], Center + x - ChildLabelWidth / 2, Center + y - ChildLabelHeight / 2);
            PaintChild(i);
            _childVisuals[i].CenterPoint = new Vector3((float)(Center + x), (float)(Center + y), 0);
            FanOut(i, _childVisuals[i]);
            FanOut(i, _childPanelVisuals[i]);
        }

        for (var i = count; i < LayoutGroup.Capacity; i++)
        {
            Settle(_children[i], _childVisuals[i]);
            Settle(_childPanels[i], _childPanelVisuals[i]);
        }

        if (previous >= 0)
        {
            Paint(previous);
        }

        if (_ringParent >= 0)
        {
            Paint(_ringParent);
        }

        ShowHub();
    }

    private WheelSlot? Slot(int i) => _model?.SlotAt(i);

    private WheelSlot? Child(int i) => _ringChildren is { } children && i >= 0 && i < children.Count ? children[i] : null;

    private WheelSlot? Under(WheelHit hit) => hit.Ring switch
    {
        WheelRing.Sector => Slot(hit.Index),
        WheelRing.Child => Child(hit.Index),
        _ => null,
    };

    private void Repaint(WheelHit hit)
    {
        switch (hit.Ring)
        {
            case WheelRing.Sector when hit.Index >= 0 && hit.Index < WheelModel.SlotCount:
                Paint(hit.Index);
                break;
            case WheelRing.Child when hit.Index >= 0 && hit.Index < LayoutGroup.Capacity:
                PaintChild(hit.Index);
                break;
        }
    }

    private void ShowHub()
    {
        var detail = Under(_hovered)?.Detail;
        _title.Text = detail?.Head ?? _model?.CenterTitle ?? "";
        _subtitle.Text = detail?.Body ?? _model?.CenterSubtitle ?? L.T("读取文件…");
        _hint.Text = _hubFirst ? L.T("先移到圆心") : _model?.HasMore == true ? L.T("打字搜索 · Esc 取消") : L.T("Esc 取消");
        _hint.Visibility = detail is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Paint(int i)
    {
        var slot = Slot(i);
        var enabled = slot?.Availability.IsAvailable == true;
        var hovered = enabled && _hovered == WheelHit.Sector(i);
        var open = i == _ringParent;
        _fills[i].Color = hovered ? Accent : open ? SectorOpen : enabled ? SectorFill : Colors.Transparent;
        _inks[i].Color = hovered ? OnAccent : Text;
        _subInks[i].Color = hovered ? OnAccent : Text3;
        _glyphs[i].Opacity = _labels[i].Opacity = _subLabels[i].Opacity = enabled || open ? 1 : 0.45;
    }

    private void PaintChild(int i)
    {
        var child = Child(i);
        var enabled = child?.Availability.IsAvailable == true;
        var hovered = enabled && _hovered == WheelHit.Child(i);
        _childFills[i].Color = hovered ? Accent : enabled ? ChildFill : ChildOff;
        _childInks[i].Color = hovered ? OnAccent : Text;
        _childSubInks[i].Color = hovered ? OnAccent : Text3;
        _childGlyphs[i].Opacity = _childLabels[i].Opacity = _childSubLabels[i].Opacity = enabled ? 1 : 0.45;
    }

    /// <summary>Lifts the sector or ring item under the pointer above its neighbours, or sets it back down.</summary>
    private void Raise(WheelHit hit, bool up)
    {
        var (element, visual) = hit.Ring switch
        {
            WheelRing.Sector when hit.Index >= 0 && hit.Index < WheelModel.SlotCount => (_sectors[hit.Index], _sectorVisuals[hit.Index]),
            WheelRing.Child when hit.Index >= 0 && hit.Index < LayoutGroup.Capacity => (_children[hit.Index], _childVisuals[hit.Index]),
            _ => ((Path?)null, (Visual?)null),
        };
        if (element is null || visual is null)
        {
            return;
        }

        var lift = up && Under(hit)?.Availability.IsAvailable == true;
        Canvas.SetZIndex(element, lift ? 1 : 0);
        var target = lift ? new Vector3(Lift, Lift, 1f) : Vector3.One;
        if (!Animated)
        {
            visual.StopAnimation("Scale");
            visual.Scale = target;
            return;
        }

        var scale = _compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1f, target, _ease);
        scale.Duration = _select;
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>A ring item appears in its turn: the <paramref name="index"/>th waits that many staggers, then grows in.</summary>
    private void FanOut(int index, Visual visual)
    {
        if (!Animated)
        {
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.Opacity = 1;
            visual.Scale = Vector3.One;
            return;
        }

        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, _ease);
        fade.Duration = _select;
        fade.DelayTime = Stagger * index;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var grow = _compositor.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(0f, new Vector3(0.9f, 0.9f, 1f));
        grow.InsertKeyFrame(1f, Vector3.One, _ease);
        grow.Duration = _select;
        grow.DelayTime = Stagger * index;
        grow.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Scale", grow);
    }

    /// <summary>Back to rest: no lift, fully opaque, in the normal z-order.</summary>
    private static void Settle(UIElement element, Visual visual)
    {
        Canvas.SetZIndex(element, 0);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.Opacity = 1;
        visual.Scale = Vector3.One;
    }

    /// <summary>A motion token from Tokens.xaml, in ms; the design's number when the resource is missing.</summary>
    private static TimeSpan Motion(string key, double fallback)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var value) && value is double ms)
            {
                return TimeSpan.FromMilliseconds(ms);
            }
        }
        catch (Exception ex)
        {
            FileLog.Error($"overlay: no {key} resource", ex);
        }

        return TimeSpan.FromMilliseconds(fallback);
    }

    private void OnFirstFrame(object? sender, RenderedEventArgs e)
    {
        CompositionTarget.Rendered -= OnFirstFrame;
        FileLog.Info($"overlay: first frame {Stopwatch.GetElapsedTime(_shownAt).TotalMilliseconds:F0} ms after show");
        if (_frames.Due)
        {
            _ = CheckFrameAsync(_generation, _bounds);
        }
    }

    /// <summary>
    /// Copies the wheel's part of the screen a moment after its first frame and judges it; a copy that looks
    /// flat is taken again a little later, and only a second flat one counts. Dropped when the wheel hid or
    /// moved meanwhile: the copy would be of whatever is under it, and a white window there would pass for a
    /// stuck driver.
    /// </summary>
    private async Task CheckFrameAsync(int generation, RectInt32 bounds)
    {
        await Task.Delay(FrameCheckDelayMs).ConfigureAwait(true);
        for (var look = 1; generation == _generation; look++)
        {
            var started = Stopwatch.GetTimestamp();
            var verdict = await Task.Run(() => ScreenCopy.Judge(bounds)).ConfigureAwait(true);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (generation != _generation)
            {
                return;
            }

            if (verdict is null)
            {
                FileLog.Info("overlay: frame check could not copy the screen");
                return;
            }

            if (verdict.Flat && look == 1)
            {
                FileLog.Info($"overlay: frame check looks {verdict}, {ms:F0} ms; looking again in {FrameRecheckMs} ms");
                await Task.Delay(FrameRecheckMs).ConfigureAwait(true);
                continue;
            }

            FileLog.Info($"overlay: frame check {verdict}, {ms:F0} ms");
            if (_frames.Record(verdict))
            {
                LooksFlat?.Invoke();
            }

            return;
        }
    }

    /// <summary>
    /// The icon font: Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10, through the theme resource
    /// that picks one; the code points the wheel uses exist in both.
    /// </summary>
    private static FontFamily SymbolFont()
    {
        try
        {
            if (Application.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var font) && font is FontFamily family)
            {
                return family;
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("overlay: no SymbolThemeFontFamily resource", ex);
        }

        return new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets");
    }

    /// <summary>A glyph block shows its one character, or takes no room, so a label without one sits centred.</summary>
    private static void SetGlyph(TextBlock block, string? glyph)
    {
        block.Text = glyph ?? "";
        block.Visibility = glyph is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A fixed-size block whose icon and two lines stay centred on (<paramref name="cx"/>, <paramref name="cy"/>) whatever is collapsed.</summary>
    private static Grid LabelPanel(TextBlock glyph, TextBlock label, TextBlock subLabel, double width, double height, double cx, double cy)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(glyph);
        stack.Children.Add(label);
        stack.Children.Add(subLabel);
        var panel = new Grid { Width = width, Height = height, IsHitTestVisible = false, Children = { stack } };
        Place(panel, cx - width / 2, cy - height / 2);
        return panel;
    }

    private static PathGeometry Wedge(int index) =>
        Band(WheelGeometry.StartAngle(index), WheelGeometry.SectorSweep, WheelGeometry.InnerRadius, WheelGeometry.OuterRadius);

    private static PathGeometry RingWedge(int parent, int count, int index) =>
        Band(WheelGeometry.ChildStartAngle(parent, count, index), WheelGeometry.ChildSweep, WheelGeometry.RingInnerRadius, WheelGeometry.RingOuterRadius);

    /// <summary>A piece of ring from <paramref name="start"/> degrees over <paramref name="sweep"/>, between two radii.</summary>
    private static PathGeometry Band(double start, double sweep, double inner, double outer)
    {
        // A hair of gap between pieces so the stroke reads as a divider.
        const double GapDeg = 0.8;
        var a0 = start + GapDeg;
        var a1 = start + sweep - GapDeg;
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
