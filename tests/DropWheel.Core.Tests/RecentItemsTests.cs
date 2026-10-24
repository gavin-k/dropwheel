using DropWheel.Core.Conversion;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class RecentItemsTests
{
    [Fact]
    public void Newest_first_without_duplicates_and_capped()
    {
        var recent = new RecentItems(capacity: 3);
        foreach (var id in new[] { "png", "webp", "gif", "png", "bmp" })
        {
            recent.Touch(MediaKind.Image, WheelMode.Convert, id);
        }

        recent.For(MediaKind.Image, WheelMode.Convert).Should().Equal("bmp", "png", "gif");
    }

    [Fact]
    public void Lists_are_kept_per_kind_and_mode()
    {
        var recent = new RecentItems();
        recent.Touch(MediaKind.Image, WheelMode.Convert, "png");

        recent.For(MediaKind.Video, WheelMode.Convert).Should().BeEmpty();
        recent.For(MediaKind.Image, WheelMode.Tools).Should().BeEmpty();
    }
}
