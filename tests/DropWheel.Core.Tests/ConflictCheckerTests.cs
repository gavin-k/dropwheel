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
}
