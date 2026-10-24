using DropWheel.Core.Input;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class WinKeyGateTests
{
    private readonly WinKeyGate _gate = new(KeyBindings.Default);

    [Fact]
    public void Swallows_down_and_up_during_a_drag()
    {
        _gate.Swallow(isDown: true, dragging: true).Should().BeTrue();
        _gate.Swallow(isDown: false, dragging: true).Should().BeTrue();
    }

    [Fact]
    public void Passes_everything_outside_a_drag()
    {
        _gate.Swallow(true, false).Should().BeFalse();
        _gate.Swallow(false, false).Should().BeFalse();
    }

    [Fact]
    public void Passes_the_up_of_a_press_that_began_before_the_drag()
    {
        _gate.Swallow(true, false).Should().BeFalse();
        _gate.Swallow(false, true).Should().BeFalse();
    }

    [Fact]
    public void Keeps_swallowing_a_press_after_the_drag_ends()
    {
        _gate.Swallow(true, true).Should().BeTrue();
        _gate.Swallow(true, false).Should().BeTrue();
        _gate.Swallow(false, false).Should().BeTrue();
        _gate.Swallow(true, false).Should().BeFalse();
    }

    [Fact]
    public void Never_swallows_when_win_is_not_bound()
    {
        var gate = new WinKeyGate(KeyBindings.Default with { Bindings = [new(WheelMode.Convert, Modifiers.Shift)] });

        gate.Swallow(true, true).Should().BeFalse();
        gate.Swallow(false, true).Should().BeFalse();
    }
}
