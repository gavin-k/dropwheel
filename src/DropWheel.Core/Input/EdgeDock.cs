using DropWheel.Core.Settings;

namespace DropWheel.Core.Input;

public enum DockSide { Left, Right }

/// <summary>
/// Says when a drag has been pushed against the left or right edge of the screen, where the dock opens to
/// collect files. Pure decision logic like <see cref="LongPress"/>: the Shell hook thread feeds it the
/// pointer moves of a drag, tests feed synthetic ones. Not thread-safe apart from <see cref="Rebind"/>;
/// call the rest from one thread.
/// </summary>
/// <remarks>
/// Only an open edge counts, one with no monitor beyond it: at the edge between two monitors the pointer
/// is on its way across. Only left and right, because Windows 11 has its own share tray along the top.
/// The caller feeds only drags that carry a drag image: dragging a window by its title bar to the edge
/// snaps it, and that gesture must not open the dock as well. Each visit to an edge counts once; the
/// pointer has to leave it and come back to count again.
/// </remarks>
public sealed class EdgeDock(KeyBindings bindings)
{
    /// <summary>How close to the edge counts as against it, in device-independent pixels.</summary>
    public const int ZoneDip = 2;

    private volatile KeyBindings _bindings = bindings;
    private DockSide? _visiting;

    /// <summary>False with the dock switched off in Settings.</summary>
    public bool Enabled => _bindings.EdgeDock;

    /// <summary>
    /// Feed a pointer move during a drag: its x, the left and (exclusive) right of its monitor, and
    /// <see cref="ZoneDip"/> in the move's pixels. <paramref name="isOpen"/> is asked once per visit, only
    /// for the edge the pointer is against. Returns the side when this move arrived at an open edge.
    /// </summary>
    public DockSide? OnMove(int x, int left, int right, int zone, Func<DockSide, bool> isOpen)
    {
        DockSide? side = !Enabled ? null : x < left + zone ? DockSide.Left : x >= right - zone ? DockSide.Right : null;
        if (side == _visiting)
        {
            return null;
        }

        _visiting = side;
        return side is { } s && isOpen(s) ? s : null;
    }

    /// <summary>The drag ended.</summary>
    public void Reset() => _visiting = null;

    /// <summary>Swaps the bindings, from any thread, when Settings change them.</summary>
    public void Rebind(KeyBindings bindings) => _bindings = bindings;
}
