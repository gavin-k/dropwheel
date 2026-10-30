using FluentAssertions;
using System.Text.Json;
using DropWheel.Core.Conversion;
using DropWheel.Core.Tools;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Tests.Tools;

/// <summary>The skill ships examples agents copy and a schema they validate against; both must agree with the reader.</summary>
public sealed class SkillExamplesTests
{
    private static readonly string Skill = Path.Combine(Root(), "skills", "dropwheel-tool");

    [Fact]
    public void Every_example_reads_without_an_error()
    {
        var folders = Directory.GetDirectories(Path.Combine(Skill, "examples"));
        folders.Should().NotBeEmpty();

        var ids = new List<string>();
        foreach (var folder in folders)
        {
            var result = ToolManifestReader.Read(folder);
            result.Problems.Where(p => !p.IsWarning).Should().BeEmpty(folder);
            result.Manifest.Should().NotBeNull(folder);
            ids.Add(result.Manifest!.Id);
            if (result.Manifest.Script is { } script)
            {
                File.Exists(Path.Combine(folder, script.Args.First(a => a.StartsWith("{toolDir}", StringComparison.Ordinal))["{toolDir}\\".Length..])).Should().BeTrue(folder);
            }
            else
            {
                result.Manifest.Steps.Should().NotBeEmpty(folder);
            }
        }

        ids.Should().OnlyHaveUniqueItems();
        Directory.GetDirectories(Path.Combine(Skill, "examples")).Select(f => ToolManifestReader.Read(f).Manifest!.Kind).Should().Contain(ToolManifest.RecipeKind, "the skill says recipes come first");
    }

    [Fact]
    public void The_schema_matches_the_reader()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "docs", "tool-manifest.schema.json")));
        var root = schema.RootElement;
        root.GetProperty("$id").GetString().Should().EndWith("/v1");
        root.GetProperty("properties").GetProperty("schema").GetProperty("const").GetInt32().Should().Be(ToolManifest.CurrentSchema);

        var kinds = root.GetProperty("properties").GetProperty("appliesTo").GetProperty("items").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        kinds.Should().BeEquivalentTo(Enum.GetValues<MediaKind>().Where(k => k != MediaKind.Unknown).Select(k => k.ToString().ToLowerInvariant()));

        var placeholders = root.GetProperty("properties").GetProperty("script").GetProperty("properties").GetProperty("args").GetProperty("description").GetString()!;
        foreach (var placeholder in ScriptSpec.Placeholders)
        {
            placeholders.Should().Contain("{" + placeholder + "}");
        }

        root.GetProperty("properties").GetProperty("script").GetProperty("properties").GetProperty("timeoutSeconds").GetProperty("default").GetInt32().Should().Be(ScriptSpec.DefaultTimeoutSeconds);
        root.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(ToolManifest.ScriptKind, ToolManifest.RecipeKind);
        root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().NotContain("script", "a recipe has steps instead");
        var step = root.GetProperty("$defs").GetProperty("step");
        step.GetProperty("properties").GetProperty("op").GetProperty("pattern").GetString().Should().Be("^(tool|action)\\.[a-z0-9-]+$");

        var icon = new System.Text.RegularExpressions.Regex(root.GetProperty("properties").GetProperty("icon").GetProperty("pattern").GetString()!);
        foreach (var text in new[] { "E7A7", "U+E7A7", "\\uE7A7", "&#xE7A7;", "\uE7A7" })
        {
            icon.IsMatch(text).Should().Be(Glyphs.Parse(text) is not null, text);
        }

        foreach (var text in new[] { "icon.svg", "rotate", "A" })
        {
            icon.IsMatch(text).Should().BeFalse(text);
        }
    }

    [Fact]
    public void The_skill_tells_the_agent_to_validate_and_points_at_the_examples()
    {
        var text = File.ReadAllText(Path.Combine(Skill, "SKILL.md"));
        text.Should().Contain(ToolCommandLine.Validate).And.Contain(ToolCommandLine.List);
        text.Should().Contain("[Console]::OutputEncoding = [Text.Encoding]::UTF8");
        foreach (var folder in Directory.GetDirectories(Path.Combine(Skill, "examples")))
        {
            text.Should().Contain(Path.GetFileName(folder));
        }
    }

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DropWheel.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("No DropWheel.slnx above " + AppContext.BaseDirectory);
    }
}
