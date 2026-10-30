using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Actions;

/// <summary>Sends the items to the Recycle Bin, where the user restores them; the job itself has no undo.</summary>
public sealed class RecycleAction(IFileOperations files) : IWheelOperation
{
    public string Id => "action.recycle";

    public WheelMode Mode => WheelMode.Actions;

    public string Label => L.T("删除");

    public string Hint => L.T("回收站");

    public string? Glyph => "\uE74D"; // Delete

    public FileAction? Action => FileAction.DeleteToRecycleBin;

    public Availability Check(ConversionRequest request) => FileActions.CheckItems(request.SourcePaths);

    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        await files.RecycleAsync(request.SourcePaths, cancellationToken).ConfigureAwait(false);
        var count = request.SourcePaths.Count;
        return new ConversionResult(true, []) { Detail = count > 1 ? L.T("已把 {0} 项移到回收站", count) : L.T("已移到回收站") };
    }
}
