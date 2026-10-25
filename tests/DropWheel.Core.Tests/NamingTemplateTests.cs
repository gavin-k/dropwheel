using DropWheel.Core.Jobs;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class NamingTemplateTests
{
    private static readonly NamingContext Ctx = new("旅行照片", "webp", "webp", new DateTimeOffset(2026, 10, 24, 9, 5, 7, TimeSpan.FromHours(8)));

    [Theory]
    [InlineData("{name}", "旅行照片")]
    [InlineData("{name}_{fmt}", "旅行照片_webp")]
    [InlineData("{name}_{date}", "旅行照片_2026-10-24")]
    [InlineData("{name}-{time}", "旅行照片-090507")]
    [InlineData("{unknown}", "{unknown}")]
    public void Expands_tokens(string template, string expected)
        => new NamingTemplate(template).Expand(Ctx).Should().Be(expected);

    [Fact]
    public void Strips_invalid_filename_characters()
        => new NamingTemplate("a:b/c").Expand(Ctx).Should().Be("a_b_c");

    [Fact]
    public void Empty_template_falls_back_to_name()
        => new NamingTemplate("   ").Expand(Ctx).Should().Be("旅行照片");

    [Fact]
    public void AutoNumber_picks_first_free_slot()
    {
        var existing = new HashSet<string> { Path.Combine("/out", "a.png"), Path.Combine("/out", "a (1).png") };
        var path = NamingTemplate.ResolveConflict("/out", "a", "png", ConflictPolicy.AutoNumber, existing.Contains);
        path.Should().Be(Path.Combine("/out", "a (2).png"));
    }

    [Fact]
    public void An_empty_extension_names_a_folder()
    {
        var existing = new HashSet<string> { Path.Combine("/out", "a") };
        NamingTemplate.ResolveConflict("/out", "a", "", ConflictPolicy.AutoNumber, existing.Contains).Should().Be(Path.Combine("/out", "a (1)"));
    }

    [Fact]
    public void Skip_returns_null_when_exists()
        => NamingTemplate.ResolveConflict("/out", "a", "png", ConflictPolicy.Skip, _ => true).Should().BeNull();
}
