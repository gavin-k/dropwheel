using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class OutputPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 5, 7, TimeSpan.FromHours(8));
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "dw-planner");
    private static readonly string Source = Path.Combine(Dir, "photo.jpg");

    [Fact]
    public void Defaults_to_the_source_folder_and_name()
        => OutputPlanner.PathFor(new([Source], "png", ""), Source, Now, _ => false)
            .Should().Be(Path.Combine(Dir, "photo.png"));

    [Fact]
    public void Uses_the_output_directory_and_template()
    {
        var other = Path.Combine(Dir, "out");
        var request = new ConversionRequest([Source], "webp", other) { Naming = "{name}_{fmt}" };
        OutputPlanner.PathFor(request, Source, Now, _ => false).Should().Be(Path.Combine(other, "photo_webp.webp"));
    }

    [Fact]
    public void Numbers_around_existing_files_by_default()
    {
        var taken = Path.Combine(Dir, "photo.png");
        OutputPlanner.PathFor(new([Source], "png", ""), Source, Now, p => p == taken)
            .Should().Be(Path.Combine(Dir, "photo (1).png"));
    }

    [Fact]
    public void Skip_policy_yields_nothing_when_taken()
        => OutputPlanner.PathFor(new ConversionRequest([Source], "png", "") { Conflict = ConflictPolicy.Skip }, Source, Now, _ => true)
            .Should().BeNull();

    [Fact]
    public void Double_extension_of_a_compressed_tar_is_dropped_whole()
    {
        var tgz = Path.Combine(Dir, "backup.tar.gz");
        OutputPlanner.PathFor(new([tgz], "zip", ""), tgz, Now, _ => false).Should().Be(Path.Combine(Dir, "backup.zip"));
    }

    [Fact]
    public void Alias_target_uses_the_canonical_extension()
        => OutputPlanner.PathFor(new([Source], "tif", ""), Source, Now, _ => false)
            .Should().Be(Path.Combine(Dir, "photo.tiff"));
}
