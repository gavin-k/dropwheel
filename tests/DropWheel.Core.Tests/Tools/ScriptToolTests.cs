using System.ComponentModel;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Tools;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests.Tools;

public sealed class ScriptToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-script-").FullName;
    private readonly string _toolDir;
    private readonly string _exe;
    private readonly FakeProcessRunner _runner = new();
    private readonly List<double> _progress = [];

    public ScriptToolTests()
    {
        _toolDir = Directory.CreateDirectory(Path.Combine(_dir, "tool")).FullName;
        _exe = Path.Combine(_toolDir, "clean.exe");
        File.WriteAllText(_exe, "");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Source(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "source");
        return path;
    }

    private IEnumerable<string> Names() => Directory.EnumerateFileSystemEntries(_dir).Select(Path.GetFileName).Order()!;

    private static ToolManifest Manifest(ScriptSpec? script = null, string undo = "recycle") => new(
        1, "user.clean", new("清理", "Clean"), new("去掉多余信息"), null, WheelMode.Tools, [MediaKind.Image], "script",
        script ?? new("clean.exe", ["--in", "{input}", "--out", "{output}", "--dir", "{outputDir}", "--tool", "{toolDir}"], true, new("new-file", "same", "-clean"), 600),
        null, undo);

    private ScriptTool Tool(ScriptSpec? script = null, string undo = "recycle") =>
        new(Manifest(script, undo), _toolDir, _runner, new CommandResolver(File.Exists, [], []), new FakeTime());

    private Task<ConversionResult> Run(ScriptTool tool, params string[] sources) =>
        tool.RunAsync(new ConversionRequest(sources, tool.Id, ""), new InlineProgress(_progress.Add), CancellationToken.None);

    private static Task<ProcessOutcome> WriteOutput(ProcessStart start, string content = "cleaned")
    {
        File.WriteAllText(FakeProcessRunner.After(start, "--out"), content);
        return Task.FromResult(new ProcessOutcome(0, ""));
    }

    [Fact]
    public void The_wheel_item_comes_from_the_manifest()
    {
        var tool = Tool();

        tool.Id.Should().Be("user.clean");
        tool.Mode.Should().Be(WheelMode.Tools);
        tool.Label.Should().Be("清理");
        tool.Hint.Should().Be("去掉多余信息");
        tool.AppliesTo(MediaKind.Image).Should().BeTrue();
        tool.AppliesTo(MediaKind.Pdf).Should().BeFalse();
        tool.TrustKey.Should().Be(ToolManifestReader.CommandHash(Manifest().Script));
        tool.Check(new([Source("a.jpg")], tool.Id, "")).Should().Be(Availability.Available);
    }

    [Fact]
    public void A_missing_command_greys_the_item_with_its_name()
    {
        File.Delete(_exe);
        var tool = Tool();

        var availability = tool.Check(new([Source("a.jpg")], tool.Id, ""));

        availability.IsAvailable.Should().BeFalse();
        availability.Reason.Should().Contain("clean.exe");
    }

    [Fact]
    public async Task The_script_output_is_moved_into_place_and_nothing_temporary_remains()
    {
        var source = Source("a.jpg");
        _runner.Behavior = (start, print, _) =>
        {
            print("progress 0.5");
            print("detail went fine");
            return WriteOutput(start);
        };

        var result = await Run(Tool(), source);

        var expected = Path.Combine(_dir, "a-clean.jpg");
        result.Succeeded.Should().BeTrue();
        result.OutputPaths.Should().Equal(expected);
        result.Detail.Should().Be("went fine");
        result.Undo.Should().BeNull("the queue recycles the outputs on undo");
        File.ReadAllText(expected).Should().Be("cleaned");
        Names().Should().Equal("a-clean.jpg", "a.jpg", "tool");
        _progress.Should().Contain(0.5).And.EndWith(1);

        var start = _runner.Starts.Should().ContainSingle().Subject;
        start.Executable.Should().Be(_exe);
        start.WorkingDirectory.Should().Be(_toolDir);
        start.Timeout.Should().Be(TimeSpan.FromSeconds(600));
        FakeProcessRunner.After(start, "--in").Should().Be(source);
        FakeProcessRunner.After(start, "--dir").Should().Be(_dir);
        FakeProcessRunner.After(start, "--tool").Should().Be(_toolDir);
        var output = FakeProcessRunner.After(start, "--out");
        Path.GetFileName(output).Should().Be("a-clean.jpg", "the script can tell the format from the name");
        Path.GetDirectoryName(Path.GetDirectoryName(output)).Should().Be(_dir, "the scratch folder sits beside the target");
        Path.GetFileName(Path.GetDirectoryName(output)).Should().EndWith(OutputFiles.TempSuffix);
        start.Environment.Should().Contain("DROPWHEEL_INPUT", source).And.Contain("DROPWHEEL_OUTPUT", output).And.Contain("DROPWHEEL_OUTPUT_DIR", _dir)
            .And.Contain("DROPWHEEL_TOOL_DIR", _toolDir).And.Contain("PYTHONIOENCODING", "utf-8").And.ContainKey("DROPWHEEL_TEMP");
    }

    [Fact]
    public async Task A_declared_extension_and_the_naming_template_shape_the_output()
    {
        var source = Source("a.jpg");
        _runner.Behavior = (start, _, _) => WriteOutput(start);
        var tool = Tool(new("clean.exe", ["--out", "{output}"], true, new("new-file", "png", ""), 600));

        var result = await tool.RunAsync(new ConversionRequest([source], tool.Id, "") { Naming = "{name}-x" }, new InlineProgress(_progress.Add), CancellationToken.None);

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "a-x.png"));
    }

    [Fact]
    public async Task A_failing_script_fails_the_job_with_its_stderr_and_leaves_nothing()
    {
        _runner.Behavior = (start, _, _) =>
        {
            File.WriteAllText(FakeProcessRunner.After(start, "--out"), "half");
            return Task.FromResult(new ProcessOutcome(2, "boom\nno such file"));
        };

        var result = await Run(Tool(), Source("a.jpg"));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("清理 失败：boom\nno such file");
        Names().Should().Equal("a.jpg", "tool");
    }

    [Fact]
    public async Task A_script_that_exits_cleanly_without_an_output_is_an_error_not_a_silent_success()
    {
        var result = await Run(Tool(), Source("a.jpg"));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("清理 没有写出文件");
        Names().Should().Equal("a.jpg", "tool");
    }

    [Fact]
    public async Task A_skip_line_skips_the_item_with_the_reason()
    {
        _runner.Behavior = (_, print, _) =>
        {
            print("skip already clean");
            return Task.FromResult(new ProcessOutcome(0, ""));
        };

        var result = await Run(Tool(), Source("a.jpg"));

        result.Succeeded.Should().BeTrue();
        result.Skipped.Should().BeTrue();
        result.OutputPaths.Should().BeEmpty();
        result.Detail.Should().Be("already clean");
        Names().Should().Equal("a.jpg", "tool");
    }

    [Fact]
    public async Task A_tool_without_output_is_done_with_nothing_to_undo()
    {
        var source = Source("a.jpg");
        var tool = Tool(new("clean.exe", ["{input}"], true, new("none", "same", ""), 60));

        var result = await Run(tool, source);

        result.Succeeded.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.OutputPaths.Should().BeEmpty();
        result.Undo.Should().BeOfType<UndoPlan.None>();
        var start = _runner.Starts.Should().ContainSingle().Subject;
        start.Arguments.Should().Equal(source);
        start.Environment.Should().NotContainKey("DROPWHEEL_OUTPUT");
        start.Timeout.Should().Be(TimeSpan.FromSeconds(60));
        Names().Should().Equal("a.jpg", "tool");
    }

    [Fact]
    public async Task Undo_none_keeps_the_output_out_of_the_recycle_bin()
    {
        _runner.Behavior = (start, _, _) => WriteOutput(start);

        var result = await Run(Tool(undo: "none"), Source("a.jpg"));

        result.OutputPaths.Should().HaveCount(1);
        result.Undo.Should().BeOfType<UndoPlan.None>();
    }

    [Fact]
    public async Task A_timed_out_script_says_so()
    {
        _runner.Behavior = (_, _, _) => Task.FromResult(new ProcessOutcome(-1, "", TimedOut: true));

        var result = await Run(Tool(), Source("a.jpg"));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("清理 超过 600 秒没有完成");
        Names().Should().Equal("a.jpg", "tool");
    }

    [Fact]
    public async Task A_command_that_cannot_start_fails_readably()
    {
        _runner.Behavior = (_, _, _) => throw new Win32Exception(2);

        var result = await Run(Tool(), Source("a.jpg"));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be($"无法启动 {_exe}");
    }

    [Fact]
    public async Task Cancellation_rolls_back_the_finished_outputs_and_leaves_nothing_temporary()
    {
        var a = Source("a.jpg");
        var b = Source("b.jpg");
        using var cts = new CancellationTokenSource();
        _runner.Behavior = (start, _, ct) =>
        {
            if (FakeProcessRunner.After(start, "--in") == b)
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return WriteOutput(start);
        };
        var tool = Tool();

        var run = () => tool.RunAsync(new([a, b], tool.Id, ""), new InlineProgress(_progress.Add), cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        Names().Should().Equal("a.jpg", "b.jpg", "tool");
    }

    [Fact]
    public async Task A_whole_batch_tool_runs_once_with_every_input_and_names_the_output_after_the_first()
    {
        var a = Source("a.jpg");
        var b = Source("b.jpg");
        _runner.Behavior = (start, _, _) => WriteOutput(start);
        var tool = Tool(new("clean.exe", ["--out", "{output}", "--in", "{inputs}"], false, new("new-file", "zip", "-all"), 600));

        var result = await Run(tool, a, b);

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "a-all.zip"));
        var start = _runner.Starts.Should().ContainSingle().Subject;
        start.Arguments.Skip(3).Should().Equal(a, b);
        start.Environment["DROPWHEEL_INPUTS"].Should().Be($"{a}\n{b}");
    }

    [Fact]
    public async Task Each_file_gets_its_own_run_and_the_progress_spans_them()
    {
        var a = Source("a.jpg");
        var b = Source("b.jpg");
        _runner.Behavior = (start, print, _) =>
        {
            print("progress 0.5");
            return WriteOutput(start);
        };

        var result = await Run(Tool(), a, b);

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "a-clean.jpg"), Path.Combine(_dir, "b-clean.jpg"));
        _runner.Starts.Select(s => FakeProcessRunner.After(s, "--in")).Should().Equal(a, b);
        _progress.Should().ContainInOrder(0.25, 0.5, 0.75, 1);
    }

    private sealed class FakeTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    }
}
