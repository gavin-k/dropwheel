using System.Drawing.Drawing2D;

namespace DragSpike;

/// <summary>
/// Six-sector drop target shown under the pointer.  Non-activating, tool window, topmost, round via
/// TransparencyKey (the magenta corners are click-through, the wheel itself is not).  WinForms'
/// AllowDrop does the RegisterDragDrop / IDropTarget work.
/// </summary>
internal sealed class WheelForm : Form
{
    public const int Sectors = 6;
    private const int LogicalDiameter = 360;

    private int _hot = -1;
    private string _centerText = "";

    /// <summary>Log time of the first paint after <see cref="ShowAt"/>.</summary>
    public double? TShown { get; private set; }

    public int Diameter { get; private set; }

    public Point CenterScreen => new(Left + Width / 2, Top + Height / 2);

    public event Action<IReadOnlyList<string>, int>? Dropped;

    public WheelForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AllowDrop = true;
        DoubleBuffered = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Size = new Size(10, 10);
        Text = "DragSpike wheel";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x00000008 /* WS_EX_TOPMOST */;
            return cp;
        }
    }

    public void ShowAt(Point screenPoint, string centerText)
    {
        Diameter = (int)Math.Round(LogicalDiameter * DeviceDpi / 96.0);
        _centerText = centerText;
        _hot = -1;
        TShown = null;
        Bounds = new Rectangle(screenPoint.X - Diameter / 2, screenPoint.Y - Diameter / 2, Diameter, Diameter);
        Show();
        Invalidate();
        Log.Info($"wheel: Show() called, bounds={Bounds} dpi={DeviceDpi}");
    }

    /// <summary>Screen point in the middle of sector <paramref name="index"/> (0 = top, clockwise).</summary>
    public Point PointForSector(int index)
    {
        var c = CenterScreen;
        var angle = (index * 60 - 90) * Math.PI / 180.0;
        var r = Diameter * 0.36;
        return new Point((int)Math.Round(c.X + Math.Cos(angle) * r), (int)Math.Round(c.Y + Math.Sin(angle) * r));
    }

    public int SectorAt(Point screen)
    {
        var c = CenterScreen;
        double dx = screen.X - c.X, dy = screen.Y - c.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < Diameter * 0.15 || dist > Diameter / 2.0)
        {
            return -1;
        }

        var deg = Math.Atan2(dy, dx) * 180.0 / Math.PI + 120.0; // sector 0 is centred on top (-90°)
        deg = ((deg % 360) + 360) % 360;
        return (int)(deg / 60);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (TShown is null)
        {
            TShown = Log.NowMs;
            Log.Info("wheel: first paint");
        }

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = ClientRectangle;
        rect.Inflate(-2, -2);
        using var dark = new SolidBrush(Color.FromArgb(32, 32, 36));
        using var hot = new SolidBrush(Color.FromArgb(194, 65, 12));
        using var line = new Pen(Color.FromArgb(70, 70, 76), 2);
        using var font = new Font("Segoe UI", 14f * DeviceDpi / 96f / (DeviceDpi / 96f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.White);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        for (var i = 0; i < Sectors; i++)
        {
            g.FillPie(i == _hot ? hot : dark, rect, i * 60 - 120, 60);
            g.DrawPie(line, rect, i * 60 - 120, 60);
            var p = PointForSector(i);
            p.Offset(-Left, -Top);
            g.DrawString($"S{i}", font, text, p, fmt);
        }

        var inner = rect;
        inner.Inflate(-(int)(rect.Width * 0.35), -(int)(rect.Height * 0.35));
        using var center = new SolidBrush(Color.FromArgb(20, 20, 24));
        g.FillEllipse(center, inner);
        g.DrawEllipse(line, inner);
        g.DrawString(_centerText, font, text, inner, fmt);
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        Log.Info($"wheel: DragEnter at {e.X},{e.Y} formats=[{string.Join(", ", e.Data?.GetFormats() ?? [])}]");
        UpdateEffect(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        UpdateEffect(e);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _hot = -1;
        Invalidate();
        Log.Info("wheel: DragLeave");
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        var paths = e.Data?.GetData(DataFormats.FileDrop) as string[] ?? [];
        var sector = SectorAt(new Point(e.X, e.Y));
        Log.Info($"wheel: DragDrop at {e.X},{e.Y} sector={sector} files=[{string.Join(", ", paths)}]");
        Hide();
        Dropped?.Invoke(paths, sector);
    }

    private void UpdateEffect(DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        var s = SectorAt(new Point(e.X, e.Y));
        if (s != _hot)
        {
            _hot = s;
            Invalidate();
            Log.Info($"wheel: DragOver sector={s} effect={e.Effect}");
        }
    }
}
