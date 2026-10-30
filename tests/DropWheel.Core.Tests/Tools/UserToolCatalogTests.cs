using DropWheel.Core.Conversion;
using DropWheel.Core.Tests.Wheel;
using DropWheel.Core.Tools;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests.Tools;

public sealed class UserToolCatalogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-catalog-").FullName;
    private readonly string _bin;

    public UserToolCatalogTests()
    {
        _bin = Directory.CreateDirectory(Path.Combine(_dir, ".bin")).FullName;
        File.WriteAllText(Path.Combine(_bin, "pwsh"), "");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private UserToolCatalog Catalog(IReadOnlyList<IWheelOperation>? builtIns = null) => new(_dir, new FakeProcessRunner(), new CommandResolver(File.Exists, [_bin], []), builtIns: builtIns);

    private static string Recipe(string steps) =>
        $$"""{ "schema": 1, "id": "user.r", "name": "配方", "wheel": "tools", "kind": "recipe", "steps": [ {{steps}} ] }""";

    private static readonly IReadOnlyList<IWheelOperation> BuiltIns =
    [
        new FakeOperation("tool.shrink", WheelMode.Tools, [MediaKind.Image]) { Options = [OptionSpec.Range("percent", "", 1, 100), OptionSpec.OneOf("fit", "", "in", "out")] },
        new FakeOperation("tool.plain", WheelMode.Tools, null),
        new FakeOperation("action.ask", WheelMode.Actions, null),
    ];

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
    public void Reload_loads_each_tool_folder_in_name_order_and_raises_Changed()
    {
        Folder("b-tool", Manifest("user.b"));
        Folder("a-tool", Manifest("user.a"));
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a folder");
        var catalog = Catalog();
        CatalogSnapshot? seen = null;
        catalog.Changed += s => seen = s;

        var snapshot = catalog.Reload();

        snapshot.Tools.Select(t => t.Id).Should().Equal("user.a", "user.b");
        snapshot.Tools[0].Folder.Should().Be(Path.Combine(_dir, "a-tool"));
        snapshot.Problems.Should().BeEmpty();
        seen.Should().BeSameAs(snapshot);
        catalog.Current.Should().BeSameAs(snapshot);
    }

    [Fact]
    public void A_broken_folder_is_reported_and_gets_a_problems_file_until_it_is_fixed()
    {
        var good = Folder("good", Manifest("user.good"));
        var bad = Folder("bad", "{ not json");
        var catalog = Catalog();

        var snapshot = catalog.Reload();

        snapshot.Tools.Select(t => t.Id).Should().Equal("user.good");
        var problem = snapshot.Problems.Should().ContainSingle().Subject;
        problem.Folder.Should().Be(bad);
        problem.Problems.Should().ContainSingle().Which.Code.Should().Be(ToolProblem.Json);
        File.Exists(Path.Combine(good, UserToolCatalog.ProblemsFile)).Should().BeFalse();
        var report = File.ReadAllText(Path.Combine(bad, UserToolCatalog.ProblemsFile));
        report.Should().Contain("\"code\": \"json\"").And.Contain("\"folder\"");

        File.WriteAllText(Path.Combine(bad, ToolManifestReader.FileName), Manifest("user.bad"));
        catalog.Reload();

        File.Exists(Path.Combine(bad, UserToolCatalog.ProblemsFile)).Should().BeFalse();
        catalog.Current.Tools.Select(t => t.Id).Should().Equal("user.bad", "user.good");
    }

    [Fact]
    public void Dot_folders_and_a_folder_without_a_manifest_are_not_tools()
    {
        Folder(".cache", Manifest("user.hidden"));
        var empty = Folder("empty", null);

        var snapshot = Catalog().Reload();

        snapshot.Tools.Should().BeEmpty();
        snapshot.Problems.Should().ContainSingle().Which.Folder.Should().Be(empty);
        File.Exists(Path.Combine(empty, UserToolCatalog.ProblemsFile)).Should().BeFalse("DropWheel does not write into a folder that was never a tool");
    }

    [Fact]
    public void A_duplicate_id_loads_once_and_the_other_folder_is_told()
    {
        Folder("first", Manifest("user.same"));
        var second = Folder("second", Manifest("user.same"));

        var snapshot = Catalog().Reload();

        snapshot.Tools.Should().ContainSingle().Which.Folder.Should().EndWith("first");
        var problem = snapshot.Problems.Should().ContainSingle().Subject;
        problem.Folder.Should().Be(second);
        problem.Problems.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Code = ToolProblem.Id, IsWarning = false });
        problem.Problems[0].Message.Should().Contain("first");
    }

    [Fact]
    public void A_command_that_cannot_be_found_is_a_warning_and_the_tool_still_loads()
    {
        var folder = Folder("py", Manifest("user.py", "python"));

        var snapshot = Catalog().Reload();

        snapshot.Tools.Should().ContainSingle().Which.Id.Should().Be("user.py");
        var problem = snapshot.Problems.Should().ContainSingle().Subject;
        problem.HasErrors.Should().BeFalse();
        problem.Problems.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Code = ToolProblem.Command, IsWarning = true });
        File.ReadAllText(Path.Combine(folder, UserToolCatalog.ProblemsFile)).Should().Contain("\"isWarning\": true");
    }

    [Fact]
    public void Falling_back_to_Windows_PowerShell_is_a_warning()
    {
        File.Move(Path.Combine(_bin, "pwsh"), Path.Combine(_bin, "powershell"));
        Folder("ps", Manifest("user.ps"));

        var snapshot = Catalog().Reload();

        snapshot.Tools.Should().ContainSingle();
        snapshot.Problems.Should().ContainSingle().Which.Problems.Should().ContainSingle().Which.Message.Should().Contain("Windows PowerShell");
    }

    [Fact]
    public void A_missing_tools_folder_is_an_empty_catalog()
    {
        var catalog = new UserToolCatalog(Path.Combine(_dir, "nowhere"), new FakeProcessRunner(), new CommandResolver(File.Exists, [], []));

        catalog.Reload().Should().BeSameAs(CatalogSnapshot.Empty);
    }

    [Fact]
    public void Own_files_do_not_count_as_changes()
    {
        UserToolCatalog.IsOwnFile(Path.Combine(_dir, "t", UserToolCatalog.ProblemsFile)).Should().BeTrue();
        UserToolCatalog.IsOwnFile(Path.Combine(_dir, "t", ".problems.json.123.tmp")).Should().BeTrue();
        UserToolCatalog.IsOwnFile(Path.Combine(_dir, "t", ".abc.dropwheel-tmp")).Should().BeTrue();
        UserToolCatalog.IsOwnFile(Path.Combine(_dir, "t", "tool.json")).Should().BeFalse();
        UserToolCatalog.IsOwnFile(Path.Combine(_dir, "t")).Should().BeFalse();
    }

    [Fact]
    public void Watch_creates_the_folder_and_can_be_disposed()
    {
        var folder = Path.Combine(_dir, "tools");
        using var catalog = new UserToolCatalog(folder, new FakeProcessRunner(), new CommandResolver(File.Exists, [], []));

        catalog.Watch();
        catalog.Watch();

        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public void A_recipe_loads_as_a_tool_that_needs_no_trust_and_takes_its_kinds_from_its_first_step()
    {
        Folder("r", Recipe("""{ "op": "tool.shrink", "options": { "percent": 30, "fit": "in" } }, { "op": "tool.plain" }"""));

        var snapshot = Catalog(BuiltIns).Reload();

        snapshot.Problems.Should().BeEmpty();
        var tool = snapshot.Tools.Should().ContainSingle().Which.Should().BeOfType<RecipeTool>().Subject;
        ((IWheelOperation)tool).TrustKey.Should().BeNull();
        tool.IsRecipeStep.Should().BeFalse();
        tool.AppliesTo(MediaKind.Image).Should().BeTrue();
        tool.AppliesTo(MediaKind.Video).Should().BeFalse();
    }

    [Fact]
    public void A_recipe_step_must_name_a_built_in_that_can_be_a_step()
    {
        Folder("r", Recipe("""{ "op": "tool.nope" }, { "op": "action.ask" }"""));

        var problems = Catalog(BuiltIns).Reload().Problems.Should().ContainSingle().Which.Problems;

        problems.Should().HaveCount(2).And.OnlyContain(p => p.Code == ToolProblem.Steps && !p.IsWarning);
        problems[0].Message.Should().Contain("第 1 步").And.Contain("tool.nope");
        problems[1].Message.Should().Contain("第 2 步").And.Contain("action.ask");
        File.Exists(Path.Combine(_dir, "r", UserToolCatalog.ProblemsFile)).Should().BeTrue();
    }

    [Fact]
    public void A_recipe_without_built_ins_to_check_against_is_an_error_not_a_crash()
    {
        Folder("r", Recipe("""{ "op": "tool.shrink" }"""));

        Catalog().Reload().Problems.Should().ContainSingle().Which.Problems.Should().ContainSingle().Which.Code.Should().Be(ToolProblem.Steps);
    }

    [Fact]
    public void Step_options_are_checked_against_what_the_built_in_takes()
    {
        Folder("r", Recipe("""{ "op": "tool.shrink", "options": { "percent": 0, "fit": "sideways", "speed": 1 } }, { "op": "tool.plain", "options": { "x": 1 } }"""));

        var problems = Catalog(BuiltIns).Reload().Problems.Should().ContainSingle().Which.Problems;

        problems.Should().HaveCount(4).And.OnlyContain(p => p.Code == ToolProblem.Steps);
        problems.Select(p => p.Message).Should().SatisfyRespectively(
            m => m.Should().Contain("percent").And.Contain("1..100"),
            m => m.Should().Contain("fit").And.Contain("in / out"),
            m => m.Should().Contain("speed").And.Contain("percent fit"),
            m => m.Should().Contain("tool.plain").And.Contain("没有选项"));
    }
}
