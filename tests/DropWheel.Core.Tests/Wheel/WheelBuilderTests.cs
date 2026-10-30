using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;
using DropWheel.Core.Tests.Tools;
using DropWheel.Core.Tools;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests.Wheel;

public sealed class WheelBuilderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-builder-").FullName;
    private readonly CommandResolver _resolver = new(_ => true, [], []);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ToolManifest Manifest(string id = "user.strip", string command = "pwsh") => new(
        ToolManifest.CurrentSchema, id, new("去水印"), new("保留原图"), null, WheelMode.Tools, [MediaKind.Image], "script",
        new ScriptSpec(command, ["{input}", "{output}"], PerFile: true, OutputSpec.Default, ScriptSpec.DefaultTimeoutSeconds), null, "recycle");

    private ScriptTool Tool(ToolManifest manifest) => new(manifest, Path.Combine(_dir, "strip"), new FakeProcessRunner(), _resolver);

    private static WheelModel Wheel(IReadOnlyList<IWheelOperation> operations, AppSettings settings) =>
        new WheelBuilder(new ConversionGraph(), new RecentItems(), new DefaultRecommender(new ConversionGraph()), operations).Build(["a.jpg"], WheelMode.Tools, settings);

    [Fact]
    public void A_script_tool_the_user_has_not_switched_on_is_greyed_with_the_cause()
    {
        var wheel = Wheel([Tool(Manifest())], AppSettings.Default);

        var slot = wheel.Slots.Should().ContainSingle().Subject;
        slot.ItemId.Should().Be("user.strip");
        slot.Label.Should().Be("去水印");
        slot.Availability.IsAvailable.Should().BeFalse();
        slot.Availability.Cause.Should().Be(UnavailableCause.UntrustedTool);
        slot.Availability.Reason.Should().Be("在 设置 › 我的工具 里启用后可用");
        slot.SubLabel.Should().Be("未启用");
        slot.Operation.Should().NotBeNull();
    }

    [Fact]
    public void Switching_it_on_for_its_current_command_makes_it_available()
    {
        var tool = Tool(Manifest());
        var settings = AppSettings.Default with { Tools = UserToolSettings.Default.Trust(tool.Id, tool.TrustKey!) };

        var slot = Wheel([tool], settings).Slots.Single();

        slot.Availability.IsAvailable.Should().BeTrue();
        slot.SubLabel.Should().Be("保留原图");
    }

    [Fact]
    public void A_changed_command_greys_it_again_until_switched_on_once_more()
    {
        var before = Tool(Manifest());
        var settings = AppSettings.Default with { Tools = UserToolSettings.Default.Trust(before.Id, before.TrustKey!) };
        var after = Tool(Manifest(command: "python"));

        var slot = Wheel([after], settings).Slots.Single();

        slot.Availability.Cause.Should().Be(UnavailableCause.UntrustedTool);
        settings.Tools.Distrust(before.Id).IsTrusted(before.Id, before.TrustKey!).Should().BeFalse();
    }

    [Fact]
    public void A_tool_with_an_icon_shows_it_on_its_sector()
    {
        var tool = Tool(Manifest() with { Icon = "U+E7A7" });
        Wheel([tool], AppSettings.Default).Slots.Single().Glyph.Should().Be("\uE7A7");
        Wheel([Tool(Manifest())], AppSettings.Default).Slots.Single().Glyph.Should().BeNull();
    }

    [Fact]
    public void A_tool_for_other_kinds_of_file_stays_off_the_wheel()
    {
        var tool = Tool(Manifest() with { AppliesTo = [MediaKind.Pdf] });

        Wheel([tool], AppSettings.Default).Slots.Should().BeEmpty();
    }

    [Fact]
    public void Live_operations_show_a_tool_written_after_the_builder_was_made()
    {
        var builtIn = Tool(Manifest("user.built-in"));
        using var catalog = new UserToolCatalog(Path.Combine(_dir, "tools"), new FakeProcessRunner(), _resolver);
        var live = new LiveOperations([builtIn], catalog);
        Wheel(live, AppSettings.Default).Slots.Select(s => s.ItemId).Should().Equal("user.built-in");

        var folder = Directory.CreateDirectory(Path.Combine(_dir, "tools", "strip")).FullName;
        File.WriteAllText(Path.Combine(folder, ToolManifestReader.FileName),
            """{ "schema": 1, "id": "user.strip", "name": "去水印", "wheel": "tools", "script": { "command": "pwsh", "args": ["{input}", "{output}"] } }""");
        catalog.Reload();

        Wheel(live, AppSettings.Default).Slots.Select(s => s.ItemId).Should().Equal("user.built-in", "user.strip");
        live.Count.Should().Be(2);
        live[1].Id.Should().Be("user.strip");
    }
}
