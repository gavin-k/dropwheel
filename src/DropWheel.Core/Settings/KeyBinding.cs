using DropWheel.Core.Wheel;

namespace DropWheel.Core.Settings;

[Flags]
public enum Modifiers { None = 0, Shift = 1, Ctrl = 2, Alt = 4, Win = 8 }

public enum InvokeStyle
{
    /// <summary>Press and release within <see cref="KeyBindings.TapWindowMs"/> while dragging. Default; does not clash with Explorer.</summary>
    Tap,
    /// <summary>Wheel opens while the modifier is held. Clashes with Explorer copy/move/shortcut on drop.</summary>
    Hold,
}

public sealed record KeyBinding(WheelMode Mode, Modifiers Keys);

public sealed record KeyBindings(
    InvokeStyle Style,
    IReadOnlyList<KeyBinding> Bindings,
    int TapWindowMs = 300,
    bool ExplorerContextMenu = true,
    bool TouchLongPress = true,
    int LongPressMs = 450,
    bool EdgeDock = true,
    bool GlobalHotkey = true)
{
    public static KeyBindings Default { get; } = new(
        InvokeStyle.Tap,
        [
            new(WheelMode.Convert, Modifiers.Shift),
            new(WheelMode.Tools, Modifiers.Ctrl | Modifiers.Alt),
            new(WheelMode.Actions, Modifiers.Win),
        ]);

    /// <summary>The keys bound to <paramref name="mode"/>, or none.</summary>
    public Modifiers KeysFor(WheelMode mode) => Bindings.FirstOrDefault(b => b.Mode == mode)?.Keys ?? Modifiers.None;

    /// <summary>These bindings with <paramref name="mode"/> on <paramref name="keys"/>, added if <paramref name="mode"/> had none.</summary>
    public KeyBindings Rebound(WheelMode mode, Modifiers keys) => this with
    {
        Bindings = Bindings.Any(b => b.Mode == mode)
            ? [.. Bindings.Select(b => b.Mode == mode ? b with { Keys = keys } : b)]
            : [.. Bindings, new(mode, keys)],
    };
}

public static class ModifierNames
{
    private static readonly (Modifiers Key, string Name)[] InWindowsOrder =
        [(Modifiers.Win, "Win"), (Modifiers.Ctrl, "Ctrl"), (Modifiers.Alt, "Alt"), (Modifiers.Shift, "Shift")];

    /// <summary>"Ctrl+Alt": the keys in the order Windows writes shortcuts, empty for none.</summary>
    public static string Of(Modifiers keys) => string.Join("+", InWindowsOrder.Where(k => (keys & k.Key) != 0).Select(k => k.Name));
}

public enum ConflictSeverity { None, Warning, Error }

public sealed record KeyConflict(KeyBinding Binding, ConflictSeverity Severity, string Message);

/// <summary>
/// Flags clashes with Explorer drag semantics and keyboard-layout quirks. Shown live in Settings.
/// The Convert wheel must have keys; any other wheel with none is turned off.
/// </summary>
public static class ConflictChecker
{
    public static IReadOnlyList<KeyConflict> Check(KeyBindings bindings, bool layoutHasAltGr = false)
    {
        var list = new List<KeyConflict>();
        var seen = new Dictionary<Modifiers, WheelMode>();

        foreach (var b in bindings.Bindings)
        {
            if (b.Keys == Modifiers.None)
            {
                if (b.Mode == WheelMode.Convert)
                {
                    list.Add(new(b, ConflictSeverity.Error, "必须至少选择一个修饰键。"));
                }

                continue;
            }

            if (seen.TryGetValue(b.Keys, out var other))
            {
                list.Add(new(b, ConflictSeverity.Error, $"与「{Name(other)}」使用了相同的按键。"));
            }

            seen[b.Keys] = b.Mode;

            if (bindings.Style == InvokeStyle.Hold && (b.Keys & (Modifiers.Ctrl | Modifiers.Shift | Modifiers.Alt)) != 0)
            {
                list.Add(new(b, ConflictSeverity.Warning, "「按住唤出」会覆盖资源管理器拖放时的复制 / 移动 / 创建快捷方式。"));
            }

            if (layoutHasAltGr && b.Keys == (Modifiers.Ctrl | Modifiers.Alt))
            {
                list.Add(new(b, ConflictSeverity.Warning, "当前键盘布局中 Ctrl+Alt 等同于 AltGr，建议改键。"));
            }
        }

        return list;
    }

    private static string Name(WheelMode mode) => mode switch
    {
        WheelMode.Convert => "转换轮盘",
        WheelMode.Tools => "工具轮盘",
        WheelMode.Actions => "动作轮盘",
        _ => mode.ToString(),
    };
}
