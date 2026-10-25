using DropWheel.Core.Input;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class LongPressTests
{
    private const int Slop = 10;
    private readonly List<WheelMode> _invoked = [];

    private LongPress Press(KeyBindings? bindings = null)
    {
        var press = new LongPress(bindings ?? KeyBindings.Default);
        press.Invoked += _invoked.Add;
        return press;
    }

    [Theory]
    [InlineData(0xFF515780UL, true)] // touch
    [InlineData(0xFF515700UL, true)] // pen
    [InlineData(0xFFFFFFFFFF515780UL, true)] // sign-extended, which the masks in the Windows docs also accept
    [InlineData(0UL, false)]
    [InlineData(0xFF515600UL, false)]
    public void Only_the_touch_and_pen_signature_counts(ulong extraInfo, bool expected)
        => LongPress.FromTouchOrPen(extraInfo).Should().Be(expected);

    [Fact]
    public void A_finger_held_still_opens_the_convert_wheel_once()
    {
        var p = Press();
        p.OnMove(100, 100, touch: true, Slop).Should().BeTrue();
        p.OnMove(106, 96, touch: true, Slop).Should().BeFalse("a few pixels of wobble is still holding");
        p.Elapsed();
        p.Elapsed();

        _invoked.Should().Equal(WheelMode.Convert);
    }

    [Fact]
    public void Moving_past_the_slop_starts_a_new_hold_that_can_open_it_again()
    {
        var p = Press();
        p.OnMove(100, 100, true, Slop);
        p.Elapsed();
        p.OnMove(111, 100, true, Slop).Should().BeTrue();
        p.Elapsed();

        _invoked.Should().HaveCount(2);
    }

    [Fact]
    public void A_mouse_never_holds_and_a_mouse_move_ends_a_finger_hold()
    {
        var p = Press();
        p.OnMove(100, 100, touch: false, Slop).Should().BeFalse();
        p.Elapsed();
        p.OnMove(100, 100, touch: true, Slop);
        p.OnMove(100, 100, touch: false, Slop);
        p.Elapsed();

        _invoked.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_opens_after_the_drag_ended()
    {
        var p = Press();
        p.OnMove(100, 100, true, Slop);
        p.Reset();
        p.Elapsed();

        _invoked.Should().BeEmpty();
    }

    [Fact]
    public void Switched_off_in_settings_it_never_starts_a_hold()
    {
        var off = KeyBindings.Default with { TouchLongPress = false };
        var p = Press(off);
        p.Hold.Should().BeNull();
        p.OnMove(100, 100, true, Slop).Should().BeFalse();
        p.Elapsed();

        p.Rebind(KeyBindings.Default with { LongPressMs = 600 });
        p.Hold.Should().Be(TimeSpan.FromMilliseconds(600));
        p.OnMove(100, 100, true, Slop).Should().BeTrue();
        p.Rebind(off);
        p.Elapsed();

        _invoked.Should().BeEmpty("switching it off ends a hold already under way");
    }
}
