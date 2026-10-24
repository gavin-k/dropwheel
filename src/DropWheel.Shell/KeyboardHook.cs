namespace DropWheel.Shell;

/// <summary>
/// Thin WH_KEYBOARD_LL wrapper. Translates VK codes to <see cref="Core.Settings.Modifiers"/> and forwards to
/// <see cref="ModifierTapper"/>. While a drag is in progress and Win is bound to a wheel, the Win key-up is
/// swallowed (return 1) so the Start menu does not open.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    public void Start(ModifierTapper tapper, Func<bool> isDragging)
    {
        // TODO(M0): SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0)
        throw new NotImplementedException("M0 spike");
    }

    public void Dispose()
    {
    }
}
