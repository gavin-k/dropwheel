using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Actions;

/// <summary>Puts the items' full paths on the clipboard, quoted and one per line, as Explorer's 复制为路径 does.</summary>
public sealed class CopyPathAction(IDesktop desktop) : IWheelOperation
{
    public string Id => "action.copy-path";

    public WheelMode Mode => WheelMode.Actions;

    public string Label => L.T("复制路径");

    public string Hint => L.T("带引号");

    public string? Glyph => "\uE71B"; // Link

    public FileAction? Action => FileAction.CopyPath;

    public Availability Check(ConversionRequest request) => Availability.Available;

    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        await desktop.SetClipboardTextAsync(Text(request.SourcePaths)).ConfigureAwait(false);
        var count = request.SourcePaths.Count;
        return new ConversionResult(true, []) { Detail = count > 1 ? L.T("已复制 {0} 个路径", count) : L.T("已复制路径") };
    }

    public static string Text(IReadOnlyList<string> paths) => string.Join("\r\n", paths.Select(p => $"\"{p}\""));
}
