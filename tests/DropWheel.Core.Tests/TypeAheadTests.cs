using DropWheel.Core.Input;
using DropWheel.Core.Settings;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class TypeAheadTests
{
    private const int B = 0x42;
    private readonly TypeAhead _search = new();
    private readonly List<char> _typed = [];

    public TypeAheadTests() => _search.Typed += _typed.Add;

    [Fact]
    public void Passes_every_key_while_not_listening()
    {
        _search.Swallow(B, isDown: true, Modifiers.None).Should().BeFalse();
        _search.Swallow(B, isDown: false, Modifiers.None).Should().BeFalse();
        _typed.Should().BeEmpty();
    }

    [Fact]
    public void Takes_letters_digits_and_backspace_as_down_up_pairs()
    {
        _search.Listening = true;

        foreach (var vk in new[] { B, 0x34, 0x64, 0x08 })
        {
            _search.Swallow(vk, true, Modifiers.None).Should().BeTrue();
            _search.Swallow(vk, false, Modifiers.None).Should().BeTrue();
        }

        _typed.Should().Equal('b', '4', '4', TypeAhead.Backspace);
    }

    [Fact]
    public void Takes_shifted_letters_in_lower_case()
    {
        _search.Listening = true;

        _search.Swallow(B, true, Modifiers.Shift).Should().BeTrue();
        _typed.Should().Equal('b');
    }

    [Theory]
    [InlineData(Modifiers.Ctrl)]
    [InlineData(Modifiers.Alt)]
    [InlineData(Modifiers.Win)]
    public void Leaves_shortcuts_alone(Modifiers held)
    {
        _search.Listening = true;

        _search.Swallow(B, true, held).Should().BeFalse();
        _search.Swallow(B, false, Modifiers.None).Should().BeFalse();
        _typed.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0x1B)] // Esc cancels the drag
    [InlineData(0x0D)]
    [InlineData(0x20)]
    [InlineData(0xBE)] // '.'
    public void Leaves_keys_a_search_ignores_alone(int vk)
    {
        _search.Listening = true;

        _search.Swallow(vk, true, Modifiers.None).Should().BeFalse();
        _typed.Should().BeEmpty();
    }

    [Fact]
    public void Auto_repeat_types_again()
    {
        _search.Listening = true;

        _search.Swallow(0x08, true, Modifiers.None);
        _search.Swallow(0x08, true, Modifiers.None).Should().BeTrue();
        _typed.Should().Equal(TypeAhead.Backspace, TypeAhead.Backspace);
    }

    [Fact]
    public void Keeps_swallowing_a_taken_key_after_the_wheel_closed()
    {
        _search.Listening = true;
        _search.Swallow(B, true, Modifiers.None);
        _search.Listening = false;

        _search.Swallow(B, true, Modifiers.None).Should().BeTrue();
        _search.Swallow(B, false, Modifiers.None).Should().BeTrue();
        _typed.Should().Equal('b');
        _search.Swallow(B, true, Modifiers.None).Should().BeFalse();
    }

    [Fact]
    public void Passes_a_key_pressed_before_the_wheel_opened_to_its_up()
    {
        _search.Swallow(B, true, Modifiers.None);
        _search.Listening = true;

        _search.Swallow(B, true, Modifiers.None).Should().BeFalse();
        _search.Swallow(B, false, Modifiers.None).Should().BeFalse();
        _typed.Should().BeEmpty();
        _search.Swallow(B, true, Modifiers.None).Should().BeTrue();
    }
}
