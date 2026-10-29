using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Input;

/// <summary>
/// Turns modifier key transitions seen during a drag into wheel invocations according to
/// <see cref="KeyBindings"/>. Pure decision logic: the Shell hook thread feeds it raw key events,
/// tests feed it synthetic ones. Not thread-safe; call it from one thread.
/// </summary>
/// <remarks>
/// A press runs from the first modifier going down to the last one coming up, so Ctrl+Alt released
/// in either order is one Ctrl+Alt tap. A tap needs the whole press to happen during the drag,
/// inside <see cref="KeyBindings.TapWindowMs"/>, with no other key pressed meanwhile (that makes it
/// a shortcut, not a tap).
/// </remarks>
public sealed class ModifierTapper(KeyBindings bindings, TimeProvider time)
{
    private volatile KeyBindings _bindings = bindings;
    private Modifiers _held = Modifiers.None;
    private Modifiers _combo = Modifiers.None;
    private long _pressedAt;
    private bool _pressedWhileDragging;
    private bool _spent;
    private readonly Dictionary<int, Modifiers> _physical = [];

    /// <summary>Raised on the calling thread when a bound combo is tapped (or held, for <see cref="InvokeStyle.Hold"/>).</summary>
    public event Action<WheelMode>? Invoked;

    /// <summary>Feed a modifier transition. <paramref name="isDown"/> is true on key-down, including auto-repeat.</summary>
    /// <param name="physicalKey">The Shell's left/right virtual-key code; zero for a logical transition.</param>
    public void OnModifier(Modifiers key, bool isDown, bool dragging, int physicalKey = 0)
    {
        var identity = physicalKey == 0 ? (int)key : physicalKey;
        if (isDown)
        {
            if (!_physical.TryAdd(identity, key) || _physical.Values.Count(k => k == key) > 1)
            {
                return;
            }
        }
        else if (!_physical.Remove(identity) || _physical.ContainsValue(key))
        {
            return;
        }

        var current = _bindings;
        if (isDown)
        {
            if (_held == Modifiers.None)
            {
                _pressedAt = time.GetTimestamp();
                _pressedWhileDragging = dragging;
                _combo = Modifiers.None;
                _spent = false;
            }

            _held |= key;
            _combo |= key;
            if (current.Style == InvokeStyle.Hold && dragging && !_spent && Bound(current, _combo) is { } held)
            {
                _spent = true;
                Invoked?.Invoke(held);
            }

            return;
        }

        if ((_held & key) == 0)
        {
            return; // pressed before we were listening
        }

        _held &= ~key;
        if (_held != Modifiers.None || current.Style != InvokeStyle.Tap)
        {
            return;
        }

        if (dragging && _pressedWhileDragging && !_spent
            && time.GetElapsedTime(_pressedAt).TotalMilliseconds <= current.TapWindowMs
            && Bound(current, _combo) is { } tapped)
        {
            Invoked?.Invoke(tapped);
        }
    }

    /// <summary>
    /// Swaps the bindings, from any thread, when Settings change them. A press already under way is judged
    /// by the bindings in place when its last key comes up.
    /// </summary>
    public void Rebind(KeyBindings bindings) => _bindings = bindings;

    /// <summary>A non-modifier key went down: the modifiers held now are part of a shortcut, not a tap.</summary>
    public void OnOtherKey()
    {
        if (_held != Modifiers.None)
        {
            _spent = true;
        }
    }

    private static WheelMode? Bound(KeyBindings bindings, Modifiers combo) => bindings.Bindings.FirstOrDefault(b => b.Keys == combo)?.Mode;
}
