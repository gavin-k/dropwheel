namespace DropWheel.Shell;

public enum DragPhase { Idle, MaybeDragging, Dragging }

/// <summary>
/// Detects a system-wide Shell drag (see ADR-0002):
///   1. WH_MOUSE_LL: left button down + movement > threshold  → MaybeDragging
///   2. UI Automation: a top-level window of class "SysDragImage" appears → Dragging
///      (WinEvents EVENT_SYSTEM_DRAGDROPSTART as a secondary signal; it is documented but unreliable)
///   3. Left button up → Idle
///
/// Hook callbacks must only post messages to the owner thread — any real work there risks the
/// system silently unhooking us.
/// </summary>
public sealed class DragMonitor : IDisposable
{
    public event Action<DragPhase>? PhaseChanged;

    public DragPhase Phase { get; private set; } = DragPhase.Idle;

    public void Start()
    {
        // TODO(M0): SetWindowsHookEx(WH_MOUSE_LL), UIA StructureChanged on RootElement (TreeScope.Children),
        // SetWinEventHook(EVENT_SYSTEM_DRAGDROPSTART..END). Debounce + throttle the UIA scan.
        throw new NotImplementedException("M0 spike");
    }

    public void Dispose()
    {
        // UnhookWindowsHookEx / UnhookWinEvent / remove UIA handler
    }

    internal void Transition(DragPhase next)
    {
        if (Phase == next)
        {
            return;
        }

        Phase = next;
        PhaseChanged?.Invoke(next);
    }
}
