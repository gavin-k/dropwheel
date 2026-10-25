using System.Collections.Immutable;

namespace DropWheel.Core.Settings;

/// <summary>The Actions wheel's items, in sector order.</summary>
public enum FileAction { CopyPath, MoveTo, CopyTo, Rename, CompressZip, DeleteToRecycleBin }

/// <summary>
/// Which Actions wheel items work. All but Delete are on by default: a slip onto any of the others asks
/// for a folder, opens a rename box or writes a zip that undo recycles, while a slip onto Delete
/// removes the user's own file.
/// </summary>
public sealed record FileActionSettings(ImmutableHashSet<FileAction> Enabled)
{
    public static FileActionSettings Default { get; } = new([FileAction.CopyPath, FileAction.MoveTo, FileAction.CopyTo, FileAction.Rename, FileAction.CompressZip]);

    public const string DisabledReason = "默认关闭 · 可在 设置 › 文件动作 中开启";

    public bool IsEnabled(FileAction action) => Enabled.Contains(action);

    public bool Equals(FileActionSettings? other) => other is not null && Enabled.SetEquals(other.Enabled);

    public override int GetHashCode() => Enabled.Aggregate(0, (h, a) => h | 1 << (int)a);
}
