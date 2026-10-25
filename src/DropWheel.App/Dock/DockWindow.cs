using DropWheel.App.Diagnostics;
using DropWheel.Core.Input;
using DropWheel.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace DropWheel.App.Dock;

/// <summary>
/// The dock: a drag pushed against the left or right edge of the screen (<see cref="EdgeDock"/>) opens a
/// strip there that files can be dropped on and left, from several folders one after another, then
/// dragged out together or one by one: onto the wheel, into a folder, into a mail. Dropping copies
/// nothing and moves nothing, the dock only remembers the paths; dragging out offers copy and link only,
/// so files never leave where they are. A file dragged out somewhere leaves the dock, which hides once
/// it is empty, and also when a drag that opened it ends without dropping anything.
///
/// A top-most WinUI window, created hidden and shown without taking focus. The drag out is WinUI's own
/// (CanDrag): one the Shell runs with DoDragDrop from here never saw a mouse message, on the UI thread or
/// on a thread of its own, and never ended. UI thread only, apart from the hook events it posts from.
/// </summary>
internal sealed class DockWindow
{
    private const double Width = 280;
    private const double Height = 360;
    private static readonly TimeSpan IdleGrace = TimeSpan.FromMilliseconds(500);

    private readonly Window _window;
    private readonly DispatcherQueue _ui;
    private readonly Func<bool> _wheelOpen;
    private readonly List<string> _files = [];
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _empty = new() { Text = "把文件放在这里，回头再拖走。", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
    private readonly Border _all;
    private readonly DispatcherQueueTimer _idleGrace;
    private bool _draggingOut;
    private volatile bool _dragImage;
    private bool _dropping;
    private bool _over;

    public DockWindow(DispatcherQueue ui, DragMonitor monitor, Func<bool> wheelOpen)
    {
        _ui = ui;
        _wheelOpen = wheelOpen;
        _window = new Window { Title = "DropWheel 停靠栏" };
        _window.AppWindow.SetIcon("Assets/DropWheel.ico");
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.IsShownInSwitchers = false;
        _window.AppWindow.Closing += (window, e) =>
        {
            if (!Quitting)
            {
                e.Cancel = true;
                Hide("closed");
            }
        };

        var clear = new Button { Content = "清空", VerticalAlignment = VerticalAlignment.Center };
        clear.Click += (_, _) =>
        {
            FileLog.Info($"dock: cleared {_files.Count} file(s)");
            _files.Clear();
            Hide("cleared");
        };
        Grid.SetColumn(clear, 1);
        _all = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            Background = Brush("AccentFillColorDefaultBrush"),
            Child = new TextBlock { Text = "全部拖出", Foreground = Brush("TextOnAccentFillColorPrimaryBrush"), HorizontalAlignment = HorizontalAlignment.Center },
        };
        DragsOut(_all, () => [.. _files]);
        Grid.SetRow(_all, 2);
        var list = new ScrollViewer { Content = _rows };
        Grid.SetRow(list, 1);

        var root = new Grid
        {
            Padding = new Thickness(12),
            RowSpacing = 10,
            AllowDrop = true,
            Background = Brush("ApplicationPageBackgroundThemeBrush"),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
            },
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
                    Children = { _title, clear },
                },
                list,
                _all,
            },
        };
        root.DragEnter += OnDragEnter;
        root.DragOver += OnDragOver;
        root.DragLeave += (_, _) => _over = false;
        root.Drop += OnDrop;
        _window.Content = root;
        Render();

        _idleGrace = ui.CreateTimer();
        _idleGrace.Interval = IdleGrace;
        _idleGrace.IsRepeating = false;
        _idleGrace.Tick += (_, _) =>
        {
            if (_files.Count == 0 && !_dropping && !_draggingOut)
            {
                Hide("the drag ended without a drop");
            }
        };

        monitor.EdgeReached += (side, x, y) => ui.TryEnqueue(() => Open(side, x, y));
        monitor.PhaseChanged += phase =>
        {
            // Whether the last drag showed the Shell's drag image, which the hook needs to see before the dock opens.
            _dragImage = phase == DragPhase.Dragging || (_dragImage && phase == DragPhase.Idle);
            if (phase == DragPhase.Idle)
            {
                ui.TryEnqueue(DragEnded);
            }
        };
    }

    /// <summary>Set before <see cref="Close"/> so the close is not turned into a hide.</summary>
    public bool Quitting { get; set; }

    public bool IsVisible => _window.AppWindow.IsVisible;

    public void Close()
    {
        Quitting = true;
        _window.Close();
    }

    /// <summary>
    /// A drag reached <paramref name="side"/> at <paramref name="x"/>, <paramref name="y"/> (physical pixels):
    /// shows the dock along that edge of the work area, centred on the pointer. Not while the wheel is open,
    /// whose drop target covers the screen, nor when the dock is already up.
    /// </summary>
    private void Open(DockSide side, int x, int y)
    {
        if (IsVisible || _wheelOpen())
        {
            return;
        }

        var monitor = PInvoke.MonitorFromPoint(new System.Drawing.Point(x, y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var scale = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded ? dpi / 96.0 : 1.0;
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var work = info.rcWork;
        var (w, h) = ((int)Math.Round(Width * scale), Math.Min((int)Math.Round(Height * scale), work.bottom - work.top));
        var left = side == DockSide.Left ? work.left : work.right - w;
        var top = Math.Clamp(y - h / 2, work.top, work.bottom - h);
        _window.AppWindow.MoveAndResize(new RectInt32(left, top, w, h));
        _window.AppWindow.Show(activateWindow: false);
        FileLog.Info($"dock: shown at the {(side == DockSide.Left ? "left" : "right")} edge, {left},{top} {w}x{h} (pointer {x},{y})");
    }

    private void DragEnded()
    {
        _over = false;
        if (IsVisible && _files.Count == 0)
        {
            _idleGrace.Start();
        }
    }

    private void Hide(string why)
    {
        _idleGrace.Stop();
        Render();
        if (IsVisible)
        {
            _window.AppWindow.Hide();
            FileLog.Info($"dock: hidden ({why})");
        }
    }

    // ---- drops onto the dock ------------------------------------------------------------------------

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!_over)
        {
            _over = true;
            FileLog.Info($"dock: entered by a drag {(_draggingOut ? "out of the dock" : e.DataView.Contains(StandardDataFormats.StorageItems) ? "of files" : "of something else")}");
        }

        OnDragOver(sender, e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // Its own files dragged back in would only be dropped where they already are.
        var takes = !_draggingOut && e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = takes ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (takes)
        {
            e.DragUIOverride.Caption = "放进停靠栏";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        _over = false;
        if (_draggingOut || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        _dropping = true;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var added = 0;
            foreach (var path in items.Select(i => i.Path).Where(p => p.Length > 0))
            {
                if (!_files.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    _files.Add(path);
                    added++;
                }
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            FileLog.Info($"dock: added {added} file(s), {_files.Count} in the dock");
        }
        catch (Exception ex)
        {
            FileLog.Error("dock: could not read the dropped files", ex);
        }
        finally
        {
            deferral.Complete();
            _dropping = false;
        }

        Render();
        if (_files.Count == 0)
        {
            Hide("nothing to keep");
        }
    }

    // ---- drags out of the dock ----------------------------------------------------------------------

    /// <summary>
    /// Makes <paramref name="element"/> drag <paramref name="paths"/> out, as files to copy or link. Files that
    /// are gone by then are forgotten instead. Each file a drop takes leaves the dock.
    /// </summary>
    private void DragsOut(UIElement element, Func<IReadOnlyList<string>> paths)
    {
        IReadOnlyList<string> dragged = [];
        element.CanDrag = true;
        element.DragStarting += async (_, e) =>
        {
            dragged = [];
            var all = paths();
            var gone = all.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
            if (gone.Count > 0)
            {
                _files.RemoveAll(gone.Contains);
                FileLog.Info($"dock: forgot {gone.Count} file(s) that are no longer there");
            }

            var present = all.Except(gone).ToList();
            if (present.Count == 0)
            {
                e.Cancel = true;
                _ui.TryEnqueue(() => Settle("nothing to keep"));
                return;
            }

            var deferral = e.GetDeferral();
            try
            {
                var items = new List<IStorageItem>(present.Count);
                foreach (var path in present)
                {
                    items.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
                }

                e.Data.SetStorageItems(items, true);
                e.Data.RequestedOperation = DataPackageOperation.Copy;
                e.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Link;
                dragged = present;
                _draggingOut = true;
                FileLog.Info($"dock: drag out of {present.Count} file(s) started");
            }
            catch (Exception ex)
            {
                FileLog.Error("dock: could not start the drag out", ex);
                e.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };
        element.DropCompleted += (_, e) =>
        {
            _draggingOut = false;
            if (dragged.Count == 0)
            {
                return;
            }

            var outcome = e.DropResult switch
            {
                DataPackageOperation.Copy => "copied",
                DataPackageOperation.Link => "linked",
                DataPackageOperation.Move => "moved",
                _ => "canceled",
            };
            FileLog.Info($"dock: dragged out {dragged.Count} file(s), {outcome}{(_dragImage ? "" : ", no drag image seen")}");
            if (e.DropResult != DataPackageOperation.None)
            {
                _files.RemoveAll(dragged.Contains);
            }

            dragged = [];
            Settle("every file was dragged out");
        };
    }

    /// <summary>Shows what is left, and hides once nothing is.</summary>
    private void Settle(string why)
    {
        Render();
        if (_files.Count == 0)
        {
            Hide(why);
        }
    }

    private void Render()
    {
        _title.Text = _files.Count == 0 ? "停靠栏" : $"停靠栏 · {_files.Count} 个文件";
        _rows.Children.Clear();
        if (_files.Count == 0)
        {
            _rows.Children.Add(_empty);
        }

        foreach (var path in _files)
        {
            var row = new Border
            {
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Background = Brush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = Brush("CardStrokeColorDefaultBrush"),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = Path.GetDirectoryName(path) ?? "", FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            DragsOut(row, () => [path]);
            _rows.Children.Add(row);
        }

        _all.Visibility = _files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
