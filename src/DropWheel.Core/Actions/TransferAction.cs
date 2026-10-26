using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Actions;

/// <summary>
/// 移动到 / 复制到: asks for a folder, then moves or copies the items into it under their own names in
/// one Shell operation. A name already there is numbered, skipped or replaced by the conflict policy,
/// asking under Ask; a replaced item goes to the Recycle Bin before the transfer and undo does not bring it
/// back. Undo moves the items back, or recycles the copies. Undo goes by the planned names: should the
/// Shell still number one because a file of that name appeared in between, undo misses that item.
/// </summary>
public sealed class TransferAction(bool copy, IFileOperations files, IDesktop desktop) : IWheelOperation
{
    /// <summary>The folder dialogs remember their last folder under these, apart from each other.</summary>
    private static readonly Guid MoveMemory = new("5b0f7d0e-3c51-4a52-9a43-0d2f6c1e7a01");
    private static readonly Guid CopyMemory = new("5b0f7d0e-3c51-4a52-9a43-0d2f6c1e7a02");

    public string Id => copy ? "action.copy-to" : "action.move-to";

    public WheelMode Mode => WheelMode.Actions;

    public string Label => copy ? L.T("复制到") : L.T("移动到");

    public string Hint => L.T("选文件夹");

    public FileAction? Action => copy ? FileAction.CopyTo : FileAction.MoveTo;

    public Availability Check(ConversionRequest request) => FileActions.CheckItems(request.SourcePaths);

    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var items = request.SourcePaths;
        var first = Path.GetFileName(Path.TrimEndingDirectorySeparator(items[0]));
        var title = (items.Count > 1, copy) switch
        {
            (true, true) => L.T("把 {0} 项复制到", items.Count),
            (true, false) => L.T("把 {0} 项移动到", items.Count),
            (false, true) => L.T("把 {0} 复制到", first),
            (false, false) => L.T("把 {0} 移动到", first),
        };
        var prompt = new FolderPrompt(title, copy ? L.T("复制到这里") : L.T("移动到这里"), Path.GetDirectoryName(items[0]) ?? items[0], copy ? CopyMemory : MoveMemory);
        if (await desktop.PickFolderAsync(prompt, cancellationToken).ConfigureAwait(false) is not { } folder)
        {
            return new ConversionResult(true, []) { Skipped = true, Detail = L.T("没有选文件夹") };
        }

        if (items.FirstOrDefault(i => Directory.Exists(i) && IsSameOrInside(folder, i)) is { } inside)
        {
            var self = Path.GetFileName(Path.TrimEndingDirectorySeparator(inside));
            return new ConversionResult(false, [], copy ? L.T("不能把 {0} 复制到它自己里面", self) : L.T("不能把 {0} 移动到它自己里面", self));
        }

        var (plan, replace, skipped) = await PlanAsync(items, folder, request).ConfigureAwait(false);
        var where = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;
        if (plan.Count == 0)
        {
            return new ConversionResult(true, []) { Skipped = true, Detail = copy ? L.T("{0} 里已有同名项，按重名策略跳过", where) : L.T("已经在 {0} 里，或那里已有同名项", where) };
        }

        foreach (var taken in replace)
        {
            await request.Conflicts!.ReplaceAsync(taken).ConfigureAwait(false);
        }

        await (copy ? files.CopyAsync(plan, cancellationToken) : files.MoveAsync(plan, cancellationToken)).ConfigureAwait(false);
        var outputs = plan.Select(m => m.To).ToList();
        return new ConversionResult(true, outputs)
        {
            Undo = copy ? null : new UndoPlan.MoveBack(plan),
            Detail = (copy ? L.T("已复制到 {0}", where) : L.T("已移动到 {0}", where)) + (skipped > 0 ? L.T("，跳过 {0} 项", skipped) : ""),
        };
    }

    /// <summary>
    /// Where each item goes, and the items already there to recycle first. A move within its own folder is
    /// skipped. A name already taken is skipped, replaced or numbered as the policy says, asking under Ask;
    /// a name an earlier item of this batch takes is skipped under Skip and numbered otherwise.
    /// </summary>
    private async Task<(List<FileMove> Plan, List<string> Replace, int Skipped)> PlanAsync(IReadOnlyList<string> items, string folder, ConversionRequest request)
    {
        var plan = new List<FileMove>();
        var replace = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(items[i]));
            if (!copy && SamePath(Path.GetDirectoryName(path), folder))
            {
                skipped++;
                continue;
            }

            var isFolder = Directory.Exists(path);
            var name = Path.GetFileName(path);
            var plain = Path.Combine(folder, name);
            var policy = request.Conflict switch
            {
                ConflictPolicy.Skip => ConflictPolicy.Skip,
                ConflictPolicy.OverwriteToRecycleBin or ConflictPolicy.Ask when !taken.Contains(plain) && Exists(plain) =>
                    await (request.Conflicts ?? throw new InvalidOperationException($"{request.Conflict} needs the request's Conflicts."))
                        .SettleAsync(plain, request.Conflict, more: i < items.Count - 1).ConfigureAwait(false),
                _ => ConflictPolicy.AutoNumber,
            };
            if (policy == ConflictPolicy.OverwriteToRecycleBin)
            {
                replace.Add(plain);
            }

            var target = policy == ConflictPolicy.OverwriteToRecycleBin ? plain : NamingTemplate.ResolveConflict(folder,
                isFolder ? name : Path.GetFileNameWithoutExtension(name),
                isFolder ? "" : Path.GetExtension(name).TrimStart('.'),
                policy, p => taken.Contains(p) || Exists(p));
            if (target is null)
            {
                skipped++;
                continue;
            }

            taken.Add(target);
            plan.Add(new FileMove(path, target));
        }

        return (plan, replace, skipped);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsSameOrInside(string folder, string item)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(item), Path.GetFullPath(folder));
        return !(relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative));
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
