using DropWheel.Core.Conversion;
using DropWheel.Core.Localization;
using DropWheel.Core.Tools;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests.Tools;

public sealed class ToolManifestReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-tool-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ToolReadResult Read(string json)
    {
        File.WriteAllText(Path.Combine(_dir, ToolManifestReader.FileName), json);
        return ToolManifestReader.Read(_dir);
    }

    private const string Minimal = """
        { "schema": 1, "id": "user.copy", "name": "复制一份", "wheel": "tools", "script": { "command": "copy.ps1", "args": ["{input}", "{output}"] } }
        """;

    [Fact]
    public void A_minimal_manifest_reads_with_defaults()
    {
        var result = Read(Minimal);

        result.Problems.Should().BeEmpty();
        var manifest = result.Manifest!;
        manifest.Id.Should().Be("user.copy");
        manifest.Wheel.Should().Be(WheelMode.Tools);
        manifest.AppliesTo.Should().BeNull();
        manifest.AppliesToKind(MediaKind.Video).Should().BeTrue();
        manifest.Kind.Should().Be("script");
        manifest.Undo.Should().Be("recycle");
        manifest.Script.PerFile.Should().BeTrue();
        manifest.Script.TimeoutSeconds.Should().Be(600);
        manifest.Script.Output.Should().Be(OutputSpec.Default);
        manifest.Hint.Zh.Should().BeEmpty();
    }

    [Fact]
    public void An_icon_is_a_code_point_and_anything_else_is_a_warning_without_one()
    {
        var good = Read(Minimal.Replace("\"wheel\"", "\"icon\": \"E7A7\", \"wheel\""));
        good.Problems.Should().BeEmpty();
        good.Manifest!.Icon.Should().Be("E7A7");

        var bad = Read(Minimal.Replace("\"wheel\"", "\"icon\": \"icon.svg\", \"wheel\""));
        bad.Manifest.Should().NotBeNull();
        bad.Problems.Should().ContainSingle(p => p.Code == ToolProblem.Icon && p.IsWarning);
    }

    [Fact]
    public void A_bare_string_name_is_Chinese_only_and_Pick_falls_back_to_it()
    {
        var name = Read(Minimal).Manifest!.Name;
        name.Should().Be(new LocalizedText("复制一份"));

        // Not through Loc.Current: that is process-wide, and the other test classes run alongside this one.
        name.Pick(UiLanguage.English).Should().Be("复制一份");
        new LocalizedText("复制一份", "Copy").Pick(UiLanguage.English).Should().Be("Copy");
        new LocalizedText("复制一份", "Copy").Pick(UiLanguage.Chinese).Should().Be("复制一份");
    }

    [Fact]
    public void Every_field_is_read()
    {
        var result = Read("""
            {
              "schema": 1, "id": "user.strip-exif", "name": { "zh": "去 EXIF", "en": "Strip EXIF" }, "hint": { "zh": "删掉拍摄信息" },
              "icon": "\uE8B9", "wheel": "actions", "appliesTo": ["image", "Video"], "kind": "script",
              "script": { "command": "C:\\tools\\strip.exe", "args": ["--in", "{inputs}", "--dir", "{outputDir}", "{toolDir}"], "perFile": false,
                          "output": { "mode": "new-file", "extension": ".jpg", "suffix": "-clean" }, "timeoutSeconds": 30 },
              "placement": { "group": "我的", "index": 5 }, "undo": "none"
            }
            """);

        result.Problems.Should().BeEmpty();
        var m = result.Manifest!;
        m.Name.Should().Be(new LocalizedText("去 EXIF", "Strip EXIF"));
        m.Hint.Should().Be(new LocalizedText("删掉拍摄信息"));
        m.Icon.Should().Be("\uE8B9");
        m.Wheel.Should().Be(WheelMode.Actions);
        m.AppliesTo.Should().Equal(MediaKind.Image, MediaKind.Video);
        m.AppliesToKind(MediaKind.Pdf).Should().BeFalse();
        m.Script.Command.Should().Be(@"C:\tools\strip.exe");
        m.Script.Args.Should().Equal("--in", "{inputs}", "--dir", "{outputDir}", "{toolDir}");
        m.Script.PerFile.Should().BeFalse();
        m.Script.Output.Should().Be(new OutputSpec("new-file", "jpg", "-clean"));
        m.Script.TimeoutSeconds.Should().Be(30);
        m.Placement.Should().Be(new PlacementHint(new("我的"), 5));
        m.Undo.Should().Be("none");
    }

    [Fact]
    public void An_id_without_the_user_prefix_is_rejected()
    {
        var result = Read(Minimal.Replace("user.copy", "png"));

        result.Manifest.Should().BeNull();
        result.Problems.Select(p => p.Code).Should().Equal(ToolProblem.Id);
        FormatRegistry.FromExtension("user.png").Should().BeNull("the prefix keeps every tool id off the format ids");
    }

    [Fact]
    public void An_unknown_placeholder_is_an_args_problem()
    {
        var result = Read(Minimal.Replace("{output}", "{out}"));

        result.Manifest.Should().BeNull();
        result.Problems.Should().ContainSingle().Which.Code.Should().Be(ToolProblem.Args);
        result.Problems[0].Message.Should().Contain("{out}").And.Contain("{outputDir}");
    }

    [Fact]
    public void An_output_placeholder_needs_a_file_output()
    {
        var result = Read(Minimal.Replace("\"perFile\": true,", "").Replace("] }", "], \"output\": { \"mode\": \"none\" } }"));

        result.Manifest.Should().BeNull();
        result.Problems.Should().ContainSingle().Which.Code.Should().Be(ToolProblem.Output);
    }

    [Fact]
    public void The_inputs_placeholder_follows_perFile()
    {
        Read(Minimal.Replace("{input}", "{inputs}")).Problems.Select(p => p.Code).Should().Equal(ToolProblem.Args);
        Read(Minimal.Replace("] }", "], \"perFile\": false }")).Problems.Select(p => p.Code).Should().Equal(ToolProblem.Args);
    }

    [Fact]
    public void Every_problem_is_listed_not_just_the_first()
    {
        var result = Read("""
            { "schema": 2, "id": "Bad Id", "wheel": "menu", "appliesTo": ["poster"], "kind": "recipe", "undo": "maybe",
              "script": { "command": "", "args": "x", "timeoutSeconds": 0, "output": { "mode": "replace", "extension": "a/b" } }, "placement": { "index": 6 } }
            """);

        result.Manifest.Should().BeNull();
        result.Problems.Select(p => p.Code).Should().BeEquivalentTo(
            [ToolProblem.Schema, ToolProblem.Id, ToolProblem.Name, ToolProblem.Wheel, ToolProblem.AppliesTo, ToolProblem.Kind, ToolProblem.Command, ToolProblem.Args, ToolProblem.Timeout, ToolProblem.Output, ToolProblem.Output, ToolProblem.Placement, ToolProblem.Undo]);
    }

    [Fact]
    public void Bad_JSON_is_a_single_json_problem()
    {
        var result = Read("{ \"schema\": 1, ");

        result.Manifest.Should().BeNull();
        result.Problems.Should().ContainSingle().Which.Code.Should().Be(ToolProblem.Json);
        result.Folder.Should().Be(_dir);
    }

    [Fact]
    public void A_folder_without_a_manifest_is_a_json_problem()
    {
        var result = ToolManifestReader.Read(_dir);

        result.Manifest.Should().BeNull();
        result.Problems.Should().ContainSingle().Which.Code.Should().Be(ToolProblem.Json);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        Read("""
            {
              // an agent left this here
              "schema": 1, "id": "user.a", "name": "甲", "wheel": "tools",
              "script": { "command": "a.ps1", "args": ["{input}", "{output}",], },
            }
            """).Problems.Should().BeEmpty();
    }

    [Fact]
    public void The_command_hash_changes_with_the_command_or_its_arguments()
    {
        var script = Read(Minimal).Manifest!.Script;
        var hash = ToolManifestReader.CommandHash(script);

        hash.Should().HaveLength(64);
        ToolManifestReader.CommandHash(script).Should().Be(hash);
        ToolManifestReader.CommandHash(script with { Command = "other.ps1" }).Should().NotBe(hash);
        ToolManifestReader.CommandHash(script with { Args = ["{input}"] }).Should().NotBe(hash);
        ToolManifestReader.CommandHash(script with { TimeoutSeconds = 1 }).Should().Be(hash);
    }
}
