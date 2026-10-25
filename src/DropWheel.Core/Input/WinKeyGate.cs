using DropWheel.Core.Settings;

namespace DropWheel.Core.Input;

/// <summary>
/// Decides whether the keyboard hook swallows a Win key event so the Start menu stays shut while a
/// drag is in progress and Win is bound to a wheel (ADR-0002 revision, item 4).
/// </summary>
/// <remarks>
/// Down and up are swallowed as a pair: a press whose down was swallowed has its repeats and its up
/// swallowed too, even if the drag ended meanwhile, and an up whose down reached Windows is passed
/// through. Otherwise Windows sees a lone down and believes Win is stuck, or a lone up and opens Start.
/// </remarks>
public sealed class WinKeyGate(KeyBindings bindings)
{
    private volatile bool _winBound = WinBound(bindings);
    private bool _swallowing;

    /// <summary>Swaps the bindings, from any thread. A press already being swallowed is swallowed to its key-up.</summary>
    public void Rebind(KeyBindings bindings) => _winBound = WinBound(bindings);

    /// <summary>True if the hook should swallow this Win key event instead of passing it on.</summary>
    public bool Swallow(bool isDown, bool dragging)
    {
        if (isDown)
        {
            _swallowing |= _winBound && dragging;
            return _swallowing;
        }

        var swallowed = _swallowing;
        _swallowing = false;
        return swallowed;
    }

    private static bool WinBound(KeyBindings bindings) => bindings.Bindings.Any(b => (b.Keys & Modifiers.Win) != 0);
}
