using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Actions;

/// <summary>
/// 移动到 / 复制到: asks for a folder, then moves or copies the items into it under their own names in
/// one Shell operation. A name already there is numbered or skipped by the conflict policy; the policies
/// the settings window does not offer are treated as numbering, so nothing is ever replaced. Undo moves
/// the items back, or recycles the copies. Undo goes by the planned names: should the Shell still number
/// one because a file of that name appeared in between, undo misses that item.
/// </summary>
public sealed class TransferAction(bool copy, IFileOperations files, IDesktop desktop) : IWheelOperation
{
    /// <summary>The folder dialogs remember their last folder under these, apart from each other.</summary>
    private static readonly Guid MoveMemory = new("5b0f7d0e-3c51-4a52-9a43-0d2f6c1e7a01");
    private static readonly Guid CopyMemory = new("5b0f7d0e-3c51-4a52-9a43-0d2f6c1e7a02");

    private string Verb => copy ? "复制" : "移动";

    public string Id => copy ? "action.copy-to" : "action.move-to";

    public WheelMode Mode => WheelMode.Actions;

    public string Label => copy ? "复制到" : "移动到";

    public string Hint => "选文件夹";

    public FileAction? Action => copy ? FileAction.CopyTo : FileAction.MoveTo;

    public Availability Check(ConversionRequest request) => FileActions.CheckItems(request.SourcePaths);

    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var items = request.SourcePaths;
        var title = items.Count > 1 ? $"把 {items.Count} 项{Verb}到" : $"把 {Path.GetFileName(Path.TrimEndingDirectorySeparator(items[0]))} {Verb}到";
        var prompt = new FolderPrompt(title, $"{Verb}到这里", Path.GetDirectoryName(items[0]) ?? items[0], copy ? CopyMemory : MoveMemory);
        if (await desktop.PickFolderAsync(prompt, cancellationToken).ConfigureAwait(false) is not { } folder)
        {
            return new ConversionResult(true, []) { Skipped = true, Detail = "没有选文件夹" };
        }

        if (items.FirstOrDefault(i => Directory.Exists(i) && IsSameOrInside(folder, i)) is { } inside)
        {
            return new ConversionResult(false, [], $"不能把 {Path.GetFileName(Path.TrimEndingDirectorySeparator(inside))} {Verb}到它自己里面");
        }

        var (plan, skipped) = Plan(items, folder, request.Conflict);
        var where = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;
        if (plan.Count == 0)
        {
            return new ConversionResult(true, []) { Skipped = true, Detail = copy ? $"{where} 里已有同名项，按重名策略跳过" : $"已经在 {where} 里，或那里已有同名项" };
        }

        await (copy ? files.CopyAsync(plan, cancellationToken) : files.MoveAsync(plan, cancellationToken)).ConfigureAwait(false);
        var outputs = plan.Select(m => m.To).ToList();
        return new ConversionResult(true, outputs)
        {
            Undo = copy ? null : new UndoPlan.MoveBack(plan),
            Detail = $"已{Verb}到 {where}" + (skipped > 0 ? $"，跳过 {skipped} 项" : ""),
        };
    }

    /// <summary>
    /// Where each item goes. A move within its own folder is skipped, as is a name already taken when the
    /// policy says skip; otherwise a taken name gets the next free number.
    /// </summary>
    private (List<FileMove> Plan, int Skipped) Plan(IReadOnlyList<string> items, string folder, ConflictPolicy conflict)
    {
        var plan = new List<FileMove>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var policy = conflict == ConflictPolicy.Skip ? ConflictPolicy.Skip : ConflictPolicy.AutoNumber;
        var skipped = 0;
        foreach (var item in items)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item));
            if (!copy && SamePath(Path.GetDirectoryName(path), folder))
            {
                skipped++;
                continue;
            }

            var isFolder = Directory.Exists(path);
            var name = Path.GetFileName(path);
            var target = NamingTemplate.ResolveConflict(folder,
                isFolder ? name : Path.GetFileNameWithoutExtension(name),
                isFolder ? "" : Path.GetExtension(name).TrimStart('.'),
                policy, p => taken.Contains(p) || File.Exists(p) || Directory.Exists(p));
            if (target is null)
            {
                skipped++;
                continue;
            }

            taken.Add(target);
            plan.Add(new FileMove(path, target));
        }

        return (plan, skipped);
    }

    private static bool IsSameOrInside(string folder, string item)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(item), Path.GetFullPath(folder));
        return !(relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative));
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
