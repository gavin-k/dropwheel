using DropWheel.Core.Input;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace DropWheel.Core.Tests;

public class ModifierTapperTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly List<WheelMode> _invoked = [];

    private ModifierTapper Tapper(KeyBindings? bindings = null)
    {
        var tapper = new ModifierTapper(bindings ?? KeyBindings.Default, _clock);
        tapper.Invoked += _invoked.Add;
        return tapper;
    }

    private void After(int ms) => _clock.Advance(TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void Shift_tap_during_a_drag_opens_the_convert_wheel()
    {
        var t = Tapper();
        t.OnModifier(Modifiers.Shift, isDown: true, dragging: true);
        After(80);
        t.OnModifier(Modifiers.Shift, isDown: false, dragging: true);

        _invoked.Should().Equal(WheelMode.Convert);
    }

    [Theory]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void Tap_must_finish_inside_the_window(int heldMs, bool expected)
    {
        var t = Tapper();
        t.OnModifier(Modifiers.Shift, true, true);
        After(heldMs);
        t.OnModifier(Modifiers.Shift, false, true);

        _invoked.Should().HaveCount(expected ? 1 : 0);
    }

    [Fact]
    public void Tap_outside_a_drag_does_nothing()
    {
        var t = Tapper();
        t.OnModifier(Modifiers.Shift, true, false);
        After(50);
        t.OnModifier(Modifiers.Shift, false, false);

        _invoked.Should().BeEmpty();
    }

    [Fact]
    public void Shift_held_before_the_drag_started_is_not_a_tap()
    {
        // Shift+drag in Explorer means "move"; letting go of Shift early must not open the wheel.
        var t = Tapper();
        t.OnModifier(Modifiers.Shift, true, false);
        After(150);
        t.OnModifier(Modifiers.Shift, false, true);

        _invoked.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ctrl_alt_tap_counts_the_whole_combo_whichever_key_comes_up_first(bool ctrlUpFirst)
    {
        var t = Tapper();
        t.OnModifier(Modifiers.Ctrl, true, true);
        After(20);
        t.OnModifier(Modifiers.Alt, true, true);
        After(60);
        t.OnModifier(ctrlUpFirst ? Modifiers.Ctrl : Modifiers.Alt, false, true);
        After(20);
        t.OnModifier(ctrlUpFirst ? Modifiers.Alt : Modifiers.Ctrl, false, true);

        _invoked.Should().Equal(WheelMode.Tools);
    }

    [Fact]
    public void Another_key_during_the_press_makes_it_a_shortcut()
    {
        var t = Tapper();
        t.OnModifier(Modifiers.Shift, true, true);
        t.OnOtherKey();
        After(50);
        t.OnModifier(Modifiers.Shift, false, true);

        _invoked.Should().BeEmpty();
    }

    [Fact]
    public void Another_key_with_no_modifier_held_does_not_spoil_the_next_tap()
    {
        var t = Tapper();
        t.OnOtherKey();
        t.OnModifier(Modifiers.Shift, true, true);
        After(50);
        t.OnModifier(Modifiers.Shift, false, true);

        _invoked.Should().Equal(WheelMode.Convert);
    }

    [Fact]
    public void A_spoiled_press_does_not_spoil_the_next_one()
    {
        var t = Tapper();
        t.OnModifier(Modifiers.Shift, true, true);
        t.OnOtherKey();
        t.OnModifier(Modifiers.Shift, false, true);
        t.OnModifier(Modifiers.Shift, true, true);
        After(50);
        t.OnModifier(Modifiers.Shift, false, true);

        _invoked.Should().Equal(WheelMode.Convert);
    }

    [Fact]
    public void Auto_repeat_does_not_restart_the_tap_window()
    {
        var t = Tapper();
        t.OnModifier(Modifiers.Shift, true, true);
        After(250);
        t.OnModifier(Modifiers.Shift, true, true);
        After(100);
        t.OnModifier(Modifiers.Shift, false, true);

        _invoked.Should().BeEmpty();
    }

    [Fact]
    public void Unbound_combo_does_nothing()
    {
        var t = Tapper();
        t.OnModifier(Modifiers.Ctrl, true, true);
        After(50);
        t.OnModifier(Modifiers.Ctrl, false, true);

        _invoked.Should().BeEmpty();
    }

    [Fact]
    public void Key_up_without_a_key_down_is_ignored()
    {
        var t = Tapper();
        t.OnModifier(Modifiers.Shift, false, true);

        _invoked.Should().BeEmpty();
    }

    [Fact]
    public void Hold_style_opens_once_on_key_down_and_ignores_repeats_and_release()
    {
        var t = Tapper(KeyBindings.Default with { Style = InvokeStyle.Hold });
        t.OnModifier(Modifiers.Win, true, true);
        After(500);
        t.OnModifier(Modifiers.Win, true, true);
        t.OnModifier(Modifiers.Win, false, true);

        _invoked.Should().Equal(WheelMode.Actions);
    }
}
