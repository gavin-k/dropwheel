namespace DropWheel.Shell;

/// <summary>
/// Owns the transparent, layered, top-most, full-virtual-screen window that is registered with
/// RegisterDragDrop while the wheel is visible. Hidden windows do not receive drag events, so this
/// window is shown exactly when the wheel opens and hidden the moment it closes.
///
/// Drop → SHCreateShellItemArrayFromDataObject → file paths → <see cref="Dropped"/>.
/// Drop on wheel centre / outside → return DROPEFFECT_NONE so the Shell treats it as a cancel.
/// </summary>
public sealed class DropTargetHost : IDisposable
{
    public event Action<IReadOnlyList<string>, (int X, int Y)>? Dropped;
    public event Action? Canceled;

    public void Show(Func<(int X, int Y), bool> isOverSector)
    {
        // TODO(M0): create HWND (WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW), OleInitialize on its thread,
        // RegisterDragDrop(hwnd, this). Map screen coordinates per-monitor DPI.
        throw new NotImplementedException("M0 spike");
    }

    public void Hide()
    {
        // RevokeDragDrop + ShowWindow(SW_HIDE)
    }

    public void Dispose() => Hide();
}
