using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;

namespace DropWheel.Shell;

/// <summary>
/// WH_KEYBOARD_LL listener that turns raw modifier key events into "tap" / "hold" decisions
/// according to <see cref="KeyBindings"/>. Pure decision logic lives here so it can be unit-tested
/// by feeding synthetic key events; the hook itself is installed by <see cref="KeyboardHook"/>.
/// </summary>
public sealed class ModifierTapper(KeyBindings bindings, TimeProvider time)
{
    private Modifiers _down = Modifiers.None;
    private long _downAtMs;

    public event Action<WheelMode>? Invoked;

    /// <summary>Feed a modifier transition. <paramref name="isDown"/> true on key-down.</summary>
    public void OnModifier(Modifiers key, bool isDown, bool dragging)
    {
        var now = time.GetTimestamp();
        if (isDown)
        {
            if (_down == Modifiers.None)
            {
                _downAtMs = now;
            }

            _down |= key;

            if (dragging && bindings.Style == InvokeStyle.Hold)
            {
                TryInvoke(_down);
            }

            return;
        }

        var held = _down;
        _down &= ~key;

        if (!dragging || bindings.Style != InvokeStyle.Tap || _down != Modifiers.None)
        {
            return;
        }

        var elapsedMs = time.GetElapsedTime(_downAtMs, now).TotalMilliseconds;
        if (elapsedMs <= bindings.TapWindowMs)
        {
            TryInvoke(held);
        }
    }

    private void TryInvoke(Modifiers combo)
    {
        var binding = bindings.Bindings.FirstOrDefault(b => b.Keys == combo);
        if (binding is not null)
        {
            Invoked?.Invoke(binding.Mode);
        }
    }
}
