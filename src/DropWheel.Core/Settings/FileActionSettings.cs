namespace DropWheel.Core.Settings;

public enum FileAction { CopyPath, MoveTo, CopyTo, Rename, CompressZip, SendTo, DeleteToRecycleBin }

/// <summary>Actions wheel: only CopyPath and MoveTo are enabled by default to avoid accidental operations.</summary>
public sealed record FileActionSettings(IReadOnlySet<FileAction> Enabled)
{
    public static FileActionSettings Default { get; } = new(new HashSet<FileAction> { FileAction.CopyPath, FileAction.MoveTo });

    public const string DisabledReason = "默认关闭 · 可在 设置 › 文件动作 中开启";
}
