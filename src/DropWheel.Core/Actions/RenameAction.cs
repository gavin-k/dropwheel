using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Actions;

/// <summary>
/// Hands the rename to Explorer: the item is selected with its name open for editing, so the user types
/// the new name where they would anyway, and Explorer's own undo covers it.
/// </summary>
public sealed class RenameAction(IDesktop desktop) : IWheelOperation
{
    public string Id => "action.rename";

    public WheelMode Mode => WheelMode.Actions;

    public string Label => L.T("重命名");

    public string Hint => L.T("资源管理器");

    public string? Glyph => "\uE8AC"; // Rename

    public FileAction? Action => FileAction.Rename;

    public Availability Check(ConversionRequest request) =>
        request.SourcePaths.Count == 1
            ? FileActions.CheckItems(request.SourcePaths)
            : Availability.Unavailable(L.T("一次只能重命名一项")) with { Cause = UnavailableCause.SelectionShape };

    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        await desktop.StartRenameAsync(request.SourcePaths[0]).ConfigureAwait(false);
        return new ConversionResult(true, []) { Detail = L.T("在资源管理器里输入新名称") };
    }
}
