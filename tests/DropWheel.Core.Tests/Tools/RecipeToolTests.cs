using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Tools;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests.Tools;

public sealed class RecipeToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-recipe-").FullName;
    private readonly List<double> _progress = [];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Source(string name, string content = "source")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private IEnumerable<string> Names() => Directory.EnumerateFileSystemEntries(_dir).Select(Path.GetFileName).Order()!;

    private static ToolManifest Manifest(params RecipeStep[] steps) => new(
        1, "user.recipe", new("配方", "Recipe"), new("两步走"), null, WheelMode.Tools, null, ToolManifest.RecipeKind, null, null, "recycle")
    { Steps = steps };

    private static RecipeStep Op(string id, params (string Name, string Value)[] options) => new(id, null, options.ToDictionary(o => o.Name, o => o.Value));

    private static RecipeStep Convert(string to) => new(null, to, new Dictionary<string, string>());

    private static RecipeTool Tool(IReadOnlyList<IWheelOperation> builtIns, ConversionGraph? graph = null, params RecipeStep[] steps) =>
        new(Manifest(steps), "/tools/recipe", builtIns, graph);

    private Task<ConversionResult> Run(RecipeTool tool, ConversionRequest request, CancellationToken cancellationToken = default) =>
        tool.RunAsync(request, new InlineProgress(_progress.Add), cancellationToken);

    private Task<ConversionResult> Run(RecipeTool tool, params string[] sources) => Run(tool, new ConversionRequest(sources, tool.Id, ""));

    /// <summary>A built-in step double: copies each source beside it with a suffix and a tag, like the image tools do.</summary>
    private sealed class Step(string id, string suffix, string? fails = null, bool skips = false, bool batch = false) : IWheelOperation
    {
        public List<ConversionRequest> Requests { get; } = [];
        public string Id => id;
        public WheelMode Mode => WheelMode.Tools;
        public string Label => id;
        public string Hint => "";
        public bool WorksOnBatch => batch;
        public IReadOnlyList<OptionSpec> Options { get; init; } = [];
        public bool AppliesTo(MediaKind kind) => kind == MediaKind.Image;
        public Availability Check(ConversionRequest request) => request.Options?.ContainsKey("broken") == true ? Availability.Unavailable("broken") : Availability.Available;

        public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (fails is not null)
            {
                return new(false, [], fails);
            }

            if (skips)
            {
                return ConversionResult.Written([]);
            }

            var sources = batch ? [request.SourcePaths[0]] : request.SourcePaths;
            var plan = request with { SourcePaths = sources, TargetFormatId = "txt", Naming = NamingTemplate.Default + suffix };
            var outputs = await OutputFiles.ForEachSourceAsync(plan, progress, (source, index, within) =>
            {
                var tag = string.Join(",", (request.Options ?? new Dictionary<string, string>()).Select(o => $"{o.Key}={o.Value}"));
                return Task.FromResult(OutputFiles.Write(plan, source, index, DateTimeOffset.UnixEpoch, temp => File.WriteAllText(temp, File.ReadAllText(source) + ">" + id + (tag.Length > 0 ? "[" + tag + "]" : "")), cancellationToken: cancellationToken));
            }, cancellationToken).ConfigureAwait(false);
            return ConversionResult.Written(outputs);
        }
    }

    [Fact]
    public async Task Steps_run_in_turn_and_only_the_last_one_writes_beside_the_source()
    {
        var a = new Step("tool.a", "-a");
        var b = new Step("tool.b", "-b");
        var tool = Tool([a, b], null, Op("tool.a", ("x", "1")), Op("tool.b"));
        var request = new ConversionRequest([Source("photo.txt")], tool.Id, "");

        var result = await Run(tool, request);

        result.Succeeded.Should().BeTrue(result.Error);
        result.OutputPaths.Should().Equal(Path.Combine(_dir, "photo-a-b.txt"));
        File.ReadAllText(result.OutputPaths[0]).Should().Be("source>tool.a[x=1]>tool.b");
        Names().Should().Equal("photo-a-b.txt", "photo.txt");
        b.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(request with { TargetFormatId = "tool.b", OutputDirectory = _dir, SourcePaths = a.Requests[0].SourcePaths }, o => o.Excluding(r => r.SourcePaths).Excluding(r => r.Options), "the last step gets the job's own request");
        a.Requests.Should().ContainSingle().Which.Options.Should().Equal(new Dictionary<string, string> { ["x"] = "1" });
        a.Requests[0].OutputDirectory.Should().StartWith(Path.Combine(_dir, ".")).And.EndWith(OutputFiles.TempSuffix);
        b.Requests[0].OutputDirectory.Should().Be(_dir);
        _progress.Should().BeInAscendingOrder().And.Contain(0.5).And.EndWith(1);
    }

    [Fact]
    public async Task A_job_folder_is_where_the_last_step_writes()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;
        var tool = Tool([new Step("tool.a", "-a"), new Step("tool.b", "-b")], null, Op("tool.a"), Op("tool.b"));

        var result = await Run(tool, new ConversionRequest([Source("photo.txt")], tool.Id, outside));

        result.OutputPaths.Should().Equal(Path.Combine(outside, "photo-a-b.txt"));
        Directory.EnumerateFileSystemEntries(outside).Should().HaveCount(1, "the scratch folder is gone");
    }

    [Fact]
    public async Task A_step_that_writes_nothing_passes_its_input_on()
    {
        var tool = Tool([new Step("tool.a", "-a", skips: true), new Step("tool.b", "-b")], null, Op("tool.a"), Op("tool.b"));

        var result = await Run(tool, Source("photo.txt"));

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "photo-b.txt"));
        File.ReadAllText(result.OutputPaths[0]).Should().Be("source>tool.b");
    }

    [Fact]
    public async Task Each_file_goes_through_a_per_file_step_and_a_batch_step_sees_them_all()
    {
        var a = new Step("tool.a", "-a");
        var zip = new Step("action.zip", "-zip", batch: true);
        var tool = Tool([a, zip], null, Op("tool.a"), Op("action.zip"));

        var result = await Run(tool, Source("one.txt"), Source("two.txt"));

        a.Requests.Select(r => r.SourcePaths.Count).Should().Equal(1, 1);
        zip.Requests.Should().ContainSingle().Which.SourcePaths.Select(Path.GetFileName).Should().Equal("one-a.txt", "two-a.txt");
        result.OutputPaths.Should().Equal(Path.Combine(_dir, "one-a-zip.txt"));
        Names().Should().Equal("one-a-zip.txt", "one.txt", "two.txt");
    }

    [Fact]
    public async Task A_failing_step_names_itself_and_leaves_nothing_behind()
    {
        var tool = Tool([new Step("tool.a", "-a"), new Step("tool.b", "-b", fails: "太大了")], null, Op("tool.a"), Op("tool.b"));

        var result = await Run(tool, Source("photo.txt"));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("第 2 步 tool.b：太大了");
        Names().Should().Equal("photo.txt");
    }

    [Fact]
    public async Task A_step_failing_on_one_of_several_files_names_the_file()
    {
        var tool = Tool([new Step("tool.a", "-a", fails: "坏"), new Step("tool.b", "-b")], null, Op("tool.a"), Op("tool.b"));

        var result = await Run(tool, Source("one.txt"), Source("two.txt"));

        result.Error.Should().Be("第 1 步 tool.a：one.txt：坏");
    }

    [Fact]
    public async Task A_step_the_wheel_does_not_have_fails_the_recipe_before_anything_runs()
    {
        var b = new Step("tool.b", "-b");
        var tool = Tool([b], null, Op("tool.a"), Op("tool.b"));

        var result = await Run(tool, Source("photo.txt"));

        result.Error.Should().Be("第 1 步 tool.a：没有叫 tool.a 的内置项能放进配方");
        b.Requests.Should().BeEmpty();
        tool.Check(new ConversionRequest([Source("x.txt")], tool.Id, "")).IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_between_steps_leaves_no_scratch_and_no_output()
    {
        using var cts = new CancellationTokenSource();
        var a = new Step("tool.a", "-a");
        var tool = Tool([a, new Step("tool.b", "-b")], null, Op("tool.a"), Op("tool.b"));
        var request = new ConversionRequest([Source("photo.txt")], tool.Id, "");
        var run = tool.RunAsync(request, new InlineProgress(p =>
        {
            if (p >= 0.5)
            {
                cts.Cancel();
            }
        }), cts.Token);

        await run.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        Names().Should().Equal("photo.txt");
    }

    [Fact]
    public async Task A_convert_step_uses_the_graph_from_the_current_inputs_format()
    {
        ConversionRequest? seen = null;
        var converter = new TestConverter(TestConverter.Between("txt", "md"), (request, progress, ct) =>
        {
            seen = request;
            var outputs = OutputFiles.ForEachSourceAsync(request, progress, (source, index, within) =>
                Task.FromResult(OutputFiles.Write(request, source, index, DateTimeOffset.UnixEpoch, temp => File.WriteAllText(temp, "md"), cancellationToken: ct)), ct).GetAwaiter().GetResult();
            return Task.FromResult(ConversionResult.Written(outputs));
        });
        var graph = new ConversionGraph();
        graph.Register(converter);
        var tool = Tool([new Step("tool.a", "-a")], graph, Op("tool.a"), Convert("md"));

        var result = await Run(tool, new ConversionRequest([Source("photo.txt")], tool.Id, "", new Dictionary<string, string> { ["from-job"] = "1" }));

        result.Succeeded.Should().BeTrue(result.Error);
        result.OutputPaths.Should().Equal(Path.Combine(_dir, "photo-a.md"));
        seen!.TargetFormatId.Should().Be("md");
        seen.Options.Should().ContainKey("from-job", "a convert step takes the job's options, not the recipe's");
    }

    [Fact]
    public async Task A_convert_step_without_an_edge_or_a_graph_fails_with_the_reason()
    {
        var graph = new ConversionGraph();
        graph.Register(new TestConverter(TestConverter.Between("png", "jpg")));

        (await Run(Tool([], graph, Convert("jpg")), Source("photo.txt"))).Error.Should().Be("第 1 步 → jpg：没有引擎支持 TXT → JPG。");
        (await Run(Tool([], null, Convert("jpg")), Source("photo.txt"))).Error.Should().Be("第 1 步 → jpg：这里没有转换引擎");
    }

    [Fact]
    public void Check_is_the_first_steps_check_with_its_options_and_applies_to_follows_the_first_step()
    {
        var tool = Tool([new Step("tool.a", "-a")], null, Op("tool.a", ("broken", "yes")), Op("tool.b"));

        tool.Check(new ConversionRequest([Path.Combine(_dir, "x.txt")], tool.Id, "")).Reason.Should().Be("broken");
        tool.AppliesTo(MediaKind.Image).Should().BeTrue();
        tool.AppliesTo(MediaKind.Pdf).Should().BeFalse();
        ((IWheelOperation)tool).TrustKey.Should().BeNull();
        tool.IsRecipeStep.Should().BeFalse();
        new RecipeTool(Manifest() with { AppliesTo = [MediaKind.Pdf] }, "/x", [], null).AppliesTo(MediaKind.Pdf).Should().BeTrue();
    }

    [Fact]
    public async Task Undo_none_turns_the_last_steps_undo_off()
    {
        var manifest = Manifest(Op("tool.a")) with { Undo = "none" };
        var tool = new RecipeTool(manifest, "/x", [new Step("tool.a", "-a")], null);

        var result = await Run(tool, Source("photo.txt"));

        result.Undo.Should().BeOfType<UndoPlan.None>();
    }

    [Fact]
    public async Task A_recipe_without_steps_fails_rather_than_doing_nothing()
    {
        var tool = Tool([]);

        (await Run(tool, Source("photo.txt"))).Error.Should().Be("配方没有步骤");
        tool.Check(new ConversionRequest([Source("y.txt")], tool.Id, "")).IsAvailable.Should().BeFalse();
    }
}
