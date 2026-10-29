using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;
using DropWheel.Core.Tests.Tools;
using DropWheel.Core.Tools;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests.Wheel;

public sealed class LayoutStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-layout-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File_ => Path.Combine(_dir, "layout.json");

    /// <summary>Six built-in image tools, as the image Tools wheel has today, and two for PDFs.</summary>
    private static readonly IReadOnlyList<IWheelOperation> BuiltIns =
    [
        .. new[] { "tool.compress-image", "tool.half-size", "tool.rotate-right", "tool.strip-metadata", "tool.combine-pdf", "tool.ocr" }.Select(id => new FakeOperation(id, WheelMode.Tools, [MediaKind.Image])),
        new FakeOperation("tool.merge-pdf", WheelMode.Tools, [MediaKind.Pdf]),
        new FakeOperation("tool.split-pdf", WheelMode.Tools, [MediaKind.Pdf]),
        new FakeOperation("action.copy-path", WheelMode.Actions, null),
    ];

    private static ToolManifest Manifest(string id, WheelMode wheel = WheelMode.Tools, IReadOnlyList<MediaKind>? appliesTo = null, PlacementHint? placement = null, bool any = false) => new(
        ToolManifest.CurrentSchema, id, new(id), new(""), null, wheel, any ? null : appliesTo ?? [MediaKind.Image], "script",
        new ScriptSpec("pwsh", ["{input}"], true, OutputSpec.Default, ScriptSpec.DefaultTimeoutSeconds), placement, "recycle");

    private static IEnumerable<string?> Ids(IReadOnlyList<LayoutSlot?>? slots) => slots!.Select(s => s?.Item ?? (s?.Group is { } g ? "group:" + g.Name.Zh + "[" + string.Join(",", g.Items) + "]" : null));

    private static WheelModel Wheel(IReadOnlyList<IWheelOperation> operations, LayoutStore layout, string path = "a.jpg") =>
        new WheelBuilder(new ConversionGraph(), new RecentItems(), new DefaultRecommender(new ConversionGraph()), operations, layout).Build([path], WheelMode.Tools, AppSettings.Default);

    [Fact]
    public void Without_a_layout_the_wheel_keeps_the_registration_order()
    {
        var store = new LayoutStore(File_);

        store.SlotsFor(WheelMode.Tools, MediaKind.Image).Should().BeNull();
        Wheel(BuiltIns, store).Slots.Select(s => s.ItemId).Should().Equal("tool.compress-image", "tool.half-size", "tool.rotate-right", "tool.strip-metadata", "tool.combine-pdf", "tool.ocr");
        store.UnplacedIds(BuiltIns).Should().BeEmpty();
        File.Exists(File_).Should().BeFalse();
    }

    [Fact]
    public void A_full_wheel_folds_the_last_sector_into_a_group_with_the_new_tool()
    {
        var store = new LayoutStore(File_);
        WheelLayout? seen = null;
        store.Changed += l => seen = l;

        store.Install(Manifest("user.strip"), BuiltIns).Should().BeTrue();

        Ids(store.SlotsFor(WheelMode.Tools, MediaKind.Image)).Should().Equal(
            "tool.compress-image", "tool.half-size", "tool.rotate-right", "tool.strip-metadata", "tool.combine-pdf", "group:更多[tool.ocr,user.strip]");
        store.SlotsFor(WheelMode.Tools, MediaKind.Pdf).Should().BeNull("the tool is for images only");
        store.Current.Installed.Should().Equal("user.strip");
        store.HasGroups(WheelMode.Tools).Should().BeTrue();
        seen.Should().BeSameAs(store.Current);
        File.Exists(File_).Should().BeTrue();
    }

    [Fact]
    public void A_wheel_with_room_takes_the_tool_at_the_hinted_sector_or_the_first_empty_one()
    {
        var store = new LayoutStore(File_);

        store.Install(Manifest("user.a", appliesTo: [MediaKind.Pdf], placement: new(null, 4)), BuiltIns).Should().BeTrue();
        store.Install(Manifest("user.b", appliesTo: [MediaKind.Pdf], placement: new(null, 4)), BuiltIns).Should().BeTrue();
        store.Install(Manifest("user.c", appliesTo: [MediaKind.Pdf]), BuiltIns).Should().BeTrue();

        Ids(store.SlotsFor(WheelMode.Tools, MediaKind.Pdf)).Should().Equal("tool.merge-pdf", "tool.split-pdf", "user.b", "user.c", "user.a", null);
    }

    [Fact]
    public void A_hinted_group_is_started_then_joined_and_only_a_full_group_leaves_a_tool_unplaced()
    {
        var store = new LayoutStore(File_);
        var mine = new LocalizedText("我的", "Mine");

        store.Install(Manifest("user.one", appliesTo: [MediaKind.Pdf], placement: new(mine, null)), BuiltIns).Should().BeTrue();
        Ids(store.SlotsFor(WheelMode.Tools, MediaKind.Pdf)).Should().Equal("tool.merge-pdf", "tool.split-pdf", "group:我的[user.one]", null, null, null);

        foreach (var n in new[] { "two", "three", "four", "five", "six" })
        {
            store.Install(Manifest("user." + n, appliesTo: [MediaKind.Pdf], placement: new(mine, 0)), BuiltIns).Should().BeTrue();
        }

        store.Install(Manifest("user.seven", appliesTo: [MediaKind.Pdf], placement: new(mine, null)), BuiltIns).Should().BeFalse();

        Ids(store.SlotsFor(WheelMode.Tools, MediaKind.Pdf)).Should().Equal("tool.merge-pdf", "tool.split-pdf", "group:我的[user.one,user.two,user.three,user.four,user.five,user.six]", null, null, null);
        store.Current.Installed.Should().Contain("user.seven", "so the hint is not applied again on every reload");
        var ops = new List<IWheelOperation>(BuiltIns) { new FakeOperation("user.seven", WheelMode.Tools, [MediaKind.Pdf]) };
        store.UnplacedIds(ops).Should().Equal("user.seven");
    }

    [Fact]
    public void A_tool_for_every_kind_of_file_lands_on_every_tools_wheel_and_an_action_on_the_one_actions_wheel()
    {
        var store = new LayoutStore(File_);

        store.Install(Manifest("user.any", any: true), BuiltIns).Should().BeTrue();
        store.Install(Manifest("user.act", WheelMode.Actions, any: true), BuiltIns).Should().BeTrue();

        Ids(store.SlotsFor(WheelMode.Tools, MediaKind.Pdf)).Should().Equal("tool.merge-pdf", "tool.split-pdf", "user.any", null, null, null);
        Ids(store.SlotsFor(WheelMode.Tools, MediaKind.Unknown)).Should().Equal("user.any", null, null, null, null, null);
        Ids(store.SlotsFor(WheelMode.Tools, MediaKind.Image)).Should().EndWith("group:更多[tool.ocr,user.any]");
        Ids(store.SlotsFor(WheelMode.Actions, MediaKind.Video)).Should().Equal("action.copy-path", "user.act", null, null, null, null);
        store.Current.Wheels["actions"].Keys.Should().Equal("any");
    }

    [Fact]
    public void The_hint_is_applied_once_and_a_hand_placed_tool_stays_where_it_is()
    {
        var store = new LayoutStore(File_);
        store.Install(Manifest("user.a", appliesTo: [MediaKind.Pdf], placement: new(null, 5)), BuiltIns);
        store.Update(l => l.With(WheelMode.Tools, MediaKind.Pdf, [new("user.a"), new("tool.merge-pdf"), new("tool.split-pdf")]));

        store.Install(Manifest("user.a", appliesTo: [MediaKind.Pdf], placement: new(null, 5)), BuiltIns).Should().BeTrue();

        Ids(store.SlotsFor(WheelMode.Tools, MediaKind.Pdf)).Should().Equal("user.a", "tool.merge-pdf", "tool.split-pdf");
    }

    [Fact]
    public void The_file_round_trips_and_a_hand_edited_one_is_repaired()
    {
        var store = new LayoutStore(File_);
        store.Install(Manifest("user.strip", placement: new(new("我的", "Mine"), null)), BuiltIns);

        var reloaded = new LayoutStore(File_);

        reloaded.LoadProblem.Should().BeNull();
        Ids(reloaded.SlotsFor(WheelMode.Tools, MediaKind.Image)).Should().Equal(Ids(store.SlotsFor(WheelMode.Tools, MediaKind.Image)));
        reloaded.SlotsFor(WheelMode.Tools, MediaKind.Image)![5]!.Group!.Name.Should().Be(new LocalizedText("我的", "Mine"));
        reloaded.Current.Installed.Should().Equal("user.strip");
        File.ReadAllText(File_).Should().NotContain("\"group\": null").And.Contain("\"image\"");

        File.WriteAllText(File_, """{ "version": 1, "wheels": { "tools": { "pdf": [ { "item": "tool.split-pdf" }, {}, { "group": { "name": { "zh": "空" }, "items": [] } }, null ] }, "actions": null }, "installed": null }""");
        var repaired = new LayoutStore(File_);

        repaired.LoadProblem.Should().BeNull();
        Ids(repaired.SlotsFor(WheelMode.Tools, MediaKind.Pdf)).Should().Equal("tool.split-pdf", null, null, null);
        repaired.Current.Installed.Should().BeEmpty();
        repaired.Current.Wheels.Keys.Should().Equal("tools");
    }

    [Fact]
    public void The_wheel_shows_the_layout_with_real_indexes_groups_and_nothing_unknown()
    {
        var store = new LayoutStore(File_);
        store.Update(l => l.With(WheelMode.Tools, MediaKind.Image,
            [null, new("tool.half-size"), new("tool.nowhere"), new(Group: new(new("我的", "Mine"), ["tool.ocr", "tool.nowhere", "tool.compress-image"])), null, new(Group: new(new("空", null), ["tool.nowhere"]))]));

        var wheel = Wheel(BuiltIns, store);

        wheel.Slots.Select(s => (s.Index, s.ItemId)).Should().Equal((1, "tool.half-size"), (3, "group:我的"));
        wheel.SlotAt(0).Should().BeNull();
        wheel.SlotAt(1)!.IsGroup.Should().BeFalse();
        var group = wheel.SlotAt(3)!;
        group.IsGroup.Should().BeTrue();
        group.Label.Should().Be("我的");
        group.SubLabel.Should().Be("2 项");
        group.Operation.Should().BeNull();
        group.Availability.IsAvailable.Should().BeTrue();
        group.Detail.Should().Be(new SlotDetail("我的", "停一下就会展开"));
        group.Children!.Select(c => (c.Index, c.ItemId)).Should().Equal((0, "tool.ocr"), (1, "tool.compress-image"));
        group.Children[1].Operation.Should().BeSameAs(BuiltIns[0]);
        store.UnplacedIds(BuiltIns).Should().Equal("tool.rotate-right", "tool.strip-metadata", "tool.combine-pdf");
    }

    [Fact]
    public void A_group_whose_items_are_all_off_is_off_and_says_so()
    {
        var store = new LayoutStore(File_);
        var off = new FakeOperation("tool.off", WheelMode.Tools, [MediaKind.Image]) { Availability = Availability.Unavailable("坏了") };
        store.Update(l => l.With(WheelMode.Tools, MediaKind.Image, [new(Group: new(new("我的"), ["tool.off"]))]));

        var group = Wheel([off], store).Slots.Single();

        group.Availability.IsAvailable.Should().BeFalse();
        group.Availability.Reason.Should().Be("里面的项现在都用不了");
        group.Detail.Should().Be(new SlotDetail("我的 1 项", "里面的项现在都用不了"));
        group.Children![0].Availability.Reason.Should().Be("坏了");
    }

    private sealed class FakeOperation(string id, WheelMode mode, IReadOnlyList<MediaKind>? kinds) : IWheelOperation
    {
        public string Id => id;
        public WheelMode Mode => mode;
        public string Label => id;
        public string Hint => "";
        public Availability Availability { get; init; } = Availability.Available;
        public bool AppliesTo(MediaKind kind) => kinds is null || kinds.Contains(kind);
        public Availability Check(ConversionRequest request) => Availability;
        public Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
