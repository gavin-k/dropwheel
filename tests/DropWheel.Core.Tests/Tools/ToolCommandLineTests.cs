using System.Text.Json;
using DropWheel.Core.Tools;
using FluentAssertions;

namespace DropWheel.Core.Tests.Tools;

public sealed class ToolCommandLineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-cli-").FullName;
    private readonly string _bin;

    public ToolCommandLineTests()
    {
        _bin = Directory.CreateDirectory(Path.Combine(_dir, ".bin")).FullName;
        File.WriteAllText(Path.Combine(_bin, "pwsh"), "");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (int Code, JsonElement Json) Run(params string[] args)
    {
        var output = new StringWriter();
        var code = ToolCommandLine.Run(args, _dir, new FakeProcessRunner(), new CommandResolver(File.Exists, [_bin], []), output);
        return (code, JsonDocument.Parse(output.ToString()).RootElement);
    }

    private string Folder(string name, string? manifest)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;
        if (manifest is not null)
        {
            File.WriteAllText(Path.Combine(folder, ToolManifestReader.FileName), manifest);
        }

        return folder;
    }

    private static string Manifest(string id, string command = "pwsh") =>
        $$"""{ "schema": 1, "id": "{{id}}", "name": "工具", "wheel": "tools", "script": { "command": "{{command}}", "args": ["{input}", "{output}"] } }""";

    [Fact]
    public void Validate_prints_the_folder_report_and_exits_0_when_it_has_no_errors()
    {
        var folder = Folder("good", Manifest("user.good", command: "python"));

        var (code, json) = Run("--validate-tool", folder);

        code.Should().Be(0);
        json.GetProperty("id").GetString().Should().Be("user.good");
        json.GetProperty("folder").GetString().Should().Be(folder);
        json.GetProperty("wheel").GetString().Should().Be("tools");
        var problems = json.GetProperty("problems").EnumerateArray().ToList();
        problems.Should().ContainSingle().Which.GetProperty("isWarning").GetBoolean().Should().BeTrue("python is not on the fake PATH, which is a warning");
        File.Exists(Path.Combine(folder, UserToolCatalog.ProblemsFile)).Should().BeFalse("validation has no side effects");
    }

    [Fact]
    public void Validate_lists_the_problems_and_exits_1_when_the_manifest_is_broken()
    {
        var folder = Folder("bad", """{ "schema": 1, "name": "工具", "wheel": "tools", "script": { "command": "pwsh", "args": [] } }""");

        var (code, json) = Run("--validate-tool", folder);

        code.Should().Be(1);
        json.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()).Should().Contain(ToolProblem.Id);
    }

    [Fact]
    public void Validate_sees_a_duplicate_id_in_a_sibling_folder()
    {
        Folder("first", Manifest("user.same"));
        var second = Folder("second", Manifest("user.same"));

        var (code, json) = Run("--validate-tool", second);

        code.Should().Be(1);
        json.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()).Should().Equal(ToolProblem.Id);
    }

    [Fact]
    public void Validate_a_missing_folder_or_no_folder_at_all_is_reported()
    {
        var (missing, json) = Run("--validate-tool", Path.Combine(_dir, "nowhere"));
        missing.Should().Be(1);
        json.GetProperty("problems").EnumerateArray().Should().ContainSingle();

        var (usage, text) = Run("--validate-tool");
        usage.Should().Be(2);
        text.GetProperty("usage").GetString().Should().Contain("--validate-tool <folder>");
    }

    [Fact]
    public void List_reports_every_folder_and_exits_1_while_one_is_broken()
    {
        Folder("b-tool", Manifest("user.b"));
        Folder("a-tool", Manifest("user.a"));
        Folder("broken", "{ not json");

        var (code, json) = Run("--list-tools");

        code.Should().Be(1);
        json.GetProperty("folder").GetString().Should().Be(_dir);
        var tools = json.GetProperty("tools").EnumerateArray().ToList();
        tools.Select(t => Path.GetFileName(t.GetProperty("folder").GetString())).Should().Equal("a-tool", "b-tool", "broken");
        tools[0].GetProperty("id").GetString().Should().Be("user.a");
        tools[0].GetProperty("problems").GetArrayLength().Should().Be(0);
        tools[2].TryGetProperty("id", out _).Should().BeFalse();
        tools[2].GetProperty("problems").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void List_without_a_tools_folder_is_empty_and_fine()
    {
        var (code, json) = Run("--list-tools");

        code.Should().Be(0);
        json.GetProperty("tools").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Only_the_two_switches_are_handled()
    {
        ToolCommandLine.Handles(["--validate-tool", "x"]).Should().BeTrue();
        ToolCommandLine.Handles(["--list-tools"]).Should().BeTrue();
        ToolCommandLine.Handles(["--data-dir", "x"]).Should().BeFalse();
    }

    [Fact]
    public void Validate_reports_a_recipe_with_its_kind_and_checks_its_steps_against_the_built_ins()
    {
        var folder = Folder("r", """{ "schema": 1, "id": "user.r", "name": "配方", "wheel": "tools", "kind": "recipe", "steps": [ { "op": "tool.a" } ] }""");
        var output = new StringWriter();

        var code = ToolCommandLine.Run([ToolCommandLine.Validate, folder], _dir, new FakeProcessRunner(), new CommandResolver(File.Exists, [_bin], []), output,
            builtIns: [new Wheel.FakeOperation("tool.a", Core.Wheel.WheelMode.Tools, null)]);

        code.Should().Be(0);
        var json = JsonDocument.Parse(output.ToString()).RootElement;
        json.GetProperty("kind").GetString().Should().Be("recipe");
        Run(ToolCommandLine.Validate, folder).Code.Should().Be(1, "without built-ins no step can be found");
    }
}
