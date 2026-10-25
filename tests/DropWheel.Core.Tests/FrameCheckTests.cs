using DropWheel.Core.Wheel;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace DropWheel.Core.Tests;

public class FrameCheckTests
{
    private const int White = 0xFFFFFF;
    private const int SolidDisc = 0x262626;

    /// <summary>The wheel window at 100%: the disc plus a 10 DIP margin, as WheelWindow sizes it.</summary>
    private const int Size = (int)(2 * (WheelGeometry.OuterRadius + 6 + 10));
    private const int DiscRadius = (int)(WheelGeometry.OuterRadius + 6);

    private static byte[] Frame(int background, Func<int, int, int?>? paint = null)
    {
        var bgra = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var colour = paint?.Invoke(x, y) ?? background;
                var i = (y * Size + x) * 4;
                bgra[i] = (byte)colour;
                bgra[i + 1] = (byte)(colour >> 8);
                bgra[i + 2] = (byte)(colour >> 16);
            }
        }

        return bgra;
    }

    private static bool InDisc(int x, int y) => Math.Pow(x - Size / 2.0, 2) + Math.Pow(y - Size / 2.0, 2) <= DiscRadius * DiscRadius;

    [Fact]
    public void A_white_block_is_flat()
    {
        var verdict = FrameCheck.Judge(Frame(White), Size, Size);
        verdict.Flat.Should().BeTrue();
        verdict.Colour.Should().Be(White);
        verdict.Share.Should().Be(1);
        verdict.ToString().Should().Be("flat, #FFFFFF on 100% of 256 samples");
    }

    [Fact]
    public void A_white_block_is_flat_with_the_drag_image_on_its_centre()
    {
        var verdict = FrameCheck.Judge(Frame(White, (x, y) => Math.Abs(x - Size / 2) < 48 && Math.Abs(y - Size / 2) < 48 ? 0x3050A0 : null), Size, Size);
        verdict.Flat.Should().BeTrue();
        verdict.Share.Should().BeLessThan(1);
    }

    [Fact]
    public void A_solid_disc_over_a_white_window_is_drawn()
    {
        var verdict = FrameCheck.Judge(Frame(White, (x, y) => InDisc(x, y) ? SolidDisc : null), Size, Size);
        verdict.Flat.Should().BeFalse();
        verdict.Colour.Should().Be(SolidDisc, "the disc covers most of the window");
        verdict.ToString().Should().StartWith("drawn, #262626 on ");
    }

    [Fact]
    public void One_dark_colour_is_not_judged_flat_since_a_solid_disc_over_a_dark_desktop_looks_so()
        => FrameCheck.Judge(Frame(SolidDisc), Size, Size).Flat.Should().BeFalse();

    [Fact]
    public void A_light_grey_block_with_a_little_noise_is_flat()
    {
        var verdict = FrameCheck.Judge(Frame(0, (x, y) => 0xF0F0F0 + ((x * 7 + y * 3) % 7 - 3) * 0x010101), Size, Size);
        verdict.Flat.Should().BeTrue();
        verdict.Share.Should().Be(1);
    }

    [Fact]
    public void A_buffer_smaller_than_the_frame_is_refused()
    {
        var act = () => FrameCheck.Judge(new byte[16], Size, Size);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static readonly FrameVerdict Drawn = new(false, SolidDisc, 0.7, 256);
    private static readonly FrameVerdict Flat = new(true, White, 1, 256);

    [Fact]
    public void The_first_show_is_checked_then_one_every_five_minutes()
    {
        var clock = new FakeTimeProvider();
        var watch = new FrameWatch(clock);
        watch.Due.Should().BeTrue();
        watch.Record(Drawn).Should().BeFalse();
        watch.Due.Should().BeFalse();
        clock.Advance(FrameWatch.Every - TimeSpan.FromSeconds(1));
        watch.Due.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        watch.Due.Should().BeTrue();
    }

    [Fact]
    public void Turning_flat_is_told_once_and_every_show_is_checked_until_one_draws()
    {
        var clock = new FakeTimeProvider();
        var watch = new FrameWatch(clock);
        watch.Record(Flat).Should().BeTrue();
        watch.Due.Should().BeTrue("a fix should be noticed at the next show");
        watch.Record(Flat).Should().BeFalse("the user was told already");
        watch.Record(Drawn).Should().BeFalse();
        watch.Due.Should().BeFalse();
        clock.Advance(FrameWatch.Every);
        watch.Record(Flat).Should().BeTrue("it broke again after a fix");
    }
}
