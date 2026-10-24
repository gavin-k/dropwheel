using System.Diagnostics;

namespace DragSpike;

/// <summary>
/// Stand-in for Explorer: a small window that starts an OLE drag (CF_HDROP with one file) once the
/// left button moves past the system drag threshold.  Prints READY/RESULT lines for the driver.
/// </summary>
internal sealed class DragSourceForm : Form
{
    private readonly string _file;
    private readonly Label _label;
    private Point _down;
    private bool _dragging;

    public DragSourceForm(string file, Point at)
    {
        _file = file;
        Text = "DragSpike source";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.Manual;
        Location = at;
        BackColor = Color.LightSteelBlue;
        _label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "drag me\n" + Path.GetFileName(file),
            Font = new Font("Segoe UI", 12f),
        };
        Controls.Add(_label);
        _label.MouseDown += OnLabelMouseDown;
        _label.MouseMove += OnLabelMouseMove;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Size = new Size((int)(240 * DeviceDpi / 96.0), (int)(240 * DeviceDpi / 96.0));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Console.WriteLine($"READY {Bounds.X} {Bounds.Y} {Bounds.Width} {Bounds.Height}");
        Console.Out.Flush();
    }

    private void OnLabelMouseDown(object? sender, MouseEventArgs e)
    {
        _down = e.Location;
        _dragging = false;
    }

    private void OnLabelMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragging || (e.Button & MouseButtons.Left) == 0)
        {
            return;
        }

        var threshold = SystemInformation.DragSize;
        if (Math.Abs(e.X - _down.X) < threshold.Width / 2 && Math.Abs(e.Y - _down.Y) < threshold.Height / 2)
        {
            return;
        }

        _dragging = true;
        var data = new DataObject(DataFormats.FileDrop, new[] { _file });
        Console.WriteLine("DRAGSTART");
        Console.Out.Flush();
        var sw = Stopwatch.StartNew();
        var effect = _label.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
        Console.WriteLine($"RESULT {effect} {sw.ElapsedMilliseconds}");
        Console.Out.Flush();
        Close();
    }
}
