using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class ConflictCheckerTests
{
    [Fact]
    public void Defaults_have_no_conflicts()
        => ConflictChecker.Check(KeyBindings.Default).Should().BeEmpty();

    [Fact]
    public void Hold_style_warns_about_explorer_keys()
    {
        var hold = KeyBindings.Default with { Style = InvokeStyle.Hold };
        var conflicts = ConflictChecker.Check(hold);
        conflicts.Should().Contain(c => c.Binding.Mode == WheelMode.Convert && c.Severity == ConflictSeverity.Warning);
        conflicts.Should().NotContain(c => c.Binding.Mode == WheelMode.Actions, "Win alone is not an Explorer drag modifier");
    }

    [Fact]
    public void Duplicate_keys_are_errors()
    {
        var dup = KeyBindings.Default with
        {
            Bindings = [new(WheelMode.Convert, Modifiers.Shift), new(WheelMode.Tools, Modifiers.Shift)],
        };
        ConflictChecker.Check(dup).Should().ContainSingle(c => c.Severity == ConflictSeverity.Error);
    }

    [Fact]
    public void AltGr_layout_warns_on_ctrl_alt()
        => ConflictChecker.Check(KeyBindings.Default, layoutHasAltGr: true)
            .Should().ContainSingle(c => c.Binding.Mode == WheelMode.Tools);

    [Fact]
    public void Rebinding_a_wheel_changes_only_its_keys_and_adds_a_missing_one()
    {
        KeyBindings.Default.Rebound(WheelMode.Convert, Modifiers.Ctrl | Modifiers.Shift).Bindings.Should().Equal(
            new KeyBinding(WheelMode.Convert, Modifiers.Ctrl | Modifiers.Shift),
            new KeyBinding(WheelMode.Tools, Modifiers.Ctrl | Modifiers.Alt),
            new KeyBinding(WheelMode.Actions, Modifiers.Win));

        var none = KeyBindings.Default with { Bindings = [] };
        none.KeysFor(WheelMode.Convert).Should().Be(Modifiers.None);
        none.Rebound(WheelMode.Convert, Modifiers.Alt).KeysFor(WheelMode.Convert).Should().Be(Modifiers.Alt);
    }

    [Theory]
    [InlineData(Modifiers.Shift, "Shift")]
    [InlineData(Modifiers.Shift | Modifiers.Ctrl, "Ctrl+Shift")]
    [InlineData(Modifiers.Shift | Modifiers.Alt | Modifiers.Ctrl | Modifiers.Win, "Win+Ctrl+Alt+Shift")]
    [InlineData(Modifiers.None, "")]
    public void Keys_are_named_in_the_order_windows_writes_shortcuts(Modifiers keys, string expected)
        => ModifierNames.Of(keys).Should().Be(expected);
}
