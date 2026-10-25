using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Input;

/// <summary>
/// Opens the Convert wheel for a finger or pen held still during a drag, where there are no keys to tap.
/// Pure decision logic like <see cref="ModifierTapper"/>: the Shell hook thread feeds it the pointer moves
/// of a drag and owns the timer, tests feed synthetic ones. Not thread-safe apart from <see cref="Rebind"/>;
/// call the rest from one thread.
/// </summary>
/// <remarks>
/// Only input Windows marks as touch or pen counts: a mouse resting mid-drag is someone thinking, and a
/// wheel popping up under it would be in the way. A hold starts on a move that leaves the last hold's
/// slop and opens the wheel once; the finger has to move off and hold again to open it again.
/// </remarks>
public sealed class LongPress(KeyBindings bindings)
{
    /// <summary>How far a held finger may wander and still be holding, in device-independent pixels.</summary>
    public const int SlopDip = 10;

    // MI_WP_SIGNATURE and SIGNATURE_MASK: Windows tags mouse input it makes from touch and pen with these.
    private const ulong TouchSignature = 0xFF515700;
    private const ulong SignatureMask = 0xFFFFFF00;

    private volatile KeyBindings _bindings = bindings;
    private (int X, int Y)? _anchor;
    private bool _spent;

    /// <summary>Raised on the calling thread when a hold has lasted.</summary>
    public event Action<WheelMode>? Invoked;

    /// <summary>How long a hold takes, or null with long-press switched off in Settings.</summary>
    public TimeSpan? Hold => _bindings is { TouchLongPress: true } b ? TimeSpan.FromMilliseconds(b.LongPressMs) : null;

    /// <summary>True if a mouse event's extra info says Windows made it from touch or pen input.</summary>
    public static bool FromTouchOrPen(ulong extraInfo) => (extraInfo & SignatureMask) == TouchSignature;

    /// <summary>
    /// Feed a pointer move during a drag; <paramref name="slop"/> is <see cref="SlopDip"/> in the move's pixels.
    /// Returns true when a hold starts here: the caller (re)starts a timer for <see cref="Hold"/> and calls
    /// <see cref="Elapsed"/> when it runs out. A move that ends the hold without starting one returns false,
    /// and the timer, if it still runs, finds nothing to open.
    /// </summary>
    public bool OnMove(int x, int y, bool touch, int slop)
    {
        if (!touch || Hold is null)
        {
            _anchor = null;
            return false;
        }

        if (_anchor is { } a && Math.Abs(x - a.X) <= slop && Math.Abs(y - a.Y) <= slop)
        {
            return false;
        }

        _anchor = (x, y);
        _spent = false;
        return true;
    }

    /// <summary>The timer for the hold that started last ran out: opens the wheel if that hold still stands.</summary>
    public void Elapsed()
    {
        if (_anchor is null || _spent || Hold is null)
        {
            return;
        }

        _spent = true;
        Invoked?.Invoke(WheelMode.Convert);
    }

    /// <summary>The drag ended.</summary>
    public void Reset() => _anchor = null;

    /// <summary>Swaps the bindings, from any thread, when Settings change them.</summary>
    public void Rebind(KeyBindings bindings) => _bindings = bindings;
}
