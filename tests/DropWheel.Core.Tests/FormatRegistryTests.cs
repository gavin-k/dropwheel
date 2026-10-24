using DropWheel.Core.Conversion;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class FormatRegistryTests
{
    [Theory]
    [InlineData("photo.JPEG", "jpg")]
    [InlineData("scan.tif", "tiff")]
    [InlineData("archive.tar.gz", "tgz")]
    [InlineData("shot.CR3", "raw")]
    public void Resolves_aliases(string path, string expectedId)
        => FormatRegistry.FromPath(path)!.Id.Should().Be(expectedId);

    [Fact]
    public void Input_only_formats_carry_a_reason()
    {
        foreach (var f in FormatRegistry.All.Where(f => !f.CanOutput))
        {
            f.InputOnlyReason.Should().NotBeNullOrWhiteSpace($"{f.Id} is input-only and must explain why");
        }
    }

    [Fact]
    public void Ids_are_unique()
        => FormatRegistry.All.Select(f => f.Id).Should().OnlyHaveUniqueItems();
}
