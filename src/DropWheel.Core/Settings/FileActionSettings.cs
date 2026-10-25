using System.Collections.Immutable;

namespace DropWheel.Core.Settings;

public enum FileAction { CopyPath, MoveTo, CopyTo, Rename, CompressZip, SendTo, DeleteToRecycleBin }

/// <summary>Actions wheel: only CopyPath and MoveTo are enabled by default to avoid accidental operations.</summary>
public sealed record FileActionSettings(ImmutableHashSet<FileAction> Enabled)
{
    public static FileActionSettings Default { get; } = new([FileAction.CopyPath, FileAction.MoveTo]);

    public const string DisabledReason = "默认关闭 · 可在 设置 › 文件动作 中开启";

    public bool IsEnabled(FileAction action) => Enabled.Contains(action);

    public bool Equals(FileActionSettings? other) => other is not null && Enabled.SetEquals(other.Enabled);

    public override int GetHashCode() => Enabled.Aggregate(0, (h, a) => h | 1 << (int)a);
}
