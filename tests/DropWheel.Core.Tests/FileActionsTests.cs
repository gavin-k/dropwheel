using System.IO.Compression;
using DropWheel.Core.Actions;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public sealed class FileActionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-actions-").FullName;
    private readonly FakeDesktop _desktop = new();
    private readonly RecordingFiles _files = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string relative, string text = "x")
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    private string Folder(string relative) => Directory.CreateDirectory(Path.Combine(_dir, relative)).FullName;

    private IReadOnlyList<IWheelOperation> Actions() => FileActions.Create(_files, _desktop);

    private IWheelOperation Action(string id) => Actions().Single(a => a.Id == id);

    private static Task<ConversionResult> Run(IWheelOperation op, params string[] paths) =>
        op.RunAsync(AppSettings.Default.Request(paths, op.Id), new InlineProgress(_ => { }), CancellationToken.None);

    private WheelModel Wheel(AppSettings settings, params string[] paths) =>
        new WheelBuilder(new ConversionGraph(), new RecentItems(), new DefaultRecommender(new ConversionGraph()), Actions()).Build(paths, WheelMode.Actions, settings);

    [Fact]
    public void The_actions_wheel_keeps_a_fixed_order_and_greys_what_settings_turned_off()
    {
        var wheel = Wheel(AppSettings.Default, File("a.jpg"));

        wheel.Mode.Should().Be(WheelMode.Actions);
        wheel.Slots.Select(s => s.ItemId).Should().Equal("action.copy-path", "action.rename", "action.zip", "action.recycle");
        wheel.Slots.Should().OnlyContain(s => s.Source == SlotSource.Fixed && s.Operation != null);
        wheel.Slots[0].SubLabel.Should().Be("带引号");

        var delete = wheel.Slots[3];
        delete.Availability.IsAvailable.Should().BeFalse();
        delete.Availability.Cause.Should().Be(UnavailableCause.DisabledInSettings);
        delete.Availability.Reason.Should().Be(FileActionSettings.DisabledReason);
        delete.SubLabel.Should().Be("未开启");
    }

    [Fact]
    public void Turning_delete_on_makes_it_available()
    {
        var settings = AppSettings.Default with { Actions = new(FileActionSettings.Default.Enabled.Add(FileAction.DeleteToRecycleBin)) };

        var wheel = Wheel(settings, File("a.jpg"));

        wheel.Slots.Should().OnlyContain(s => s.Availability.IsAvailable);
        wheel.Slots[3].SubLabel.Should().Be("回收站");
    }

    [Fact]
    public void Rename_takes_exactly_one_item()
    {
        var wheel = Wheel(AppSettings.Default, File("a.jpg"), File("b.jpg"));

        var rename = wheel.Slots.Single(s => s.ItemId == "action.rename");
        rename.Availability.Cause.Should().Be(UnavailableCause.SelectionShape);
        rename.SubLabel.Should().Be("不可用");
        wheel.CenterSubtitle.Should().Be("动作 · 2 项");
    }

    [Fact]
    public void An_item_that_is_gone_makes_the_file_actions_unavailable()
    {
        var gone = Path.Combine(_dir, "gone.jpg");

        Action("action.zip").Check(new([gone], "action.zip", "")).IsAvailable.Should().BeFalse();
        Action("action.recycle").Check(new([gone], "action.recycle", "")).IsAvailable.Should().BeFalse();
        Action("action.copy-path").Check(new([gone], "action.copy-path", "")).IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void A_whole_drive_is_refused()
    {
        var root = Path.GetPathRoot(_dir)!;

        Action("action.recycle").Check(new([root], "action.recycle", "")).Cause.Should().Be(UnavailableCause.SelectionShape);
        Action("action.zip").Check(new([root], "action.zip", "")).IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Copy_path_puts_quoted_paths_one_per_line_on_the_clipboard()
    {
        var a = File("a b.jpg");
        var b = Folder("c");

        var result = await Run(Action("action.copy-path"), a, b);

        _desktop.Clipboard.Should().Be($"\"{a}\"\r\n\"{b}\"");
        result.Succeeded.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.OutputPaths.Should().BeEmpty();
        result.Detail.Should().Be("已复制 2 个路径");
    }

    [Fact]
    public async Task Rename_opens_the_item_for_editing()
    {
        var a = File("a.jpg");

        var result = await Run(Action("action.rename"), a);

        _desktop.Renamed.Should().Equal(a);
        result.Skipped.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_recycles_the_items_and_leaves_no_undo()
    {
        var a = File("a.jpg");
        var b = Folder("b");

        var result = await Run(Action("action.recycle"), a, b);

        _files.Recycled.Should().Equal(a, b);
        result.OutputPaths.Should().BeEmpty();
        result.Undo.Should().BeNull();
        result.Detail.Should().Be("已把 2 项移到回收站");
    }

    [Fact]
    public async Task Zip_of_a_file_is_named_after_it_and_holds_it()
    {
        var a = File("report.final.docx", "hello");

        var result = await Run(Action("action.zip"), a);

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "report.final.zip"));
        Entries(result.OutputPaths[0]).Should().Equal("report.final.docx");
        Read(result.OutputPaths[0], "report.final.docx").Should().Be("hello");
        Directory.GetFileSystemEntries(_dir).Should().NotContain(p => p.EndsWith(OutputFiles.TempSuffix));
    }

    [Fact]
    public async Task Zip_of_a_folder_keeps_its_whole_name_its_tree_and_its_empty_folders()
    {
        var folder = Folder("Photos.v2");
        File("Photos.v2/a.jpg", "a");
        File("Photos.v2/sub/b.jpg", "b");
        Folder("Photos.v2/empty");

        var result = await Run(Action("action.zip"), folder);

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "Photos.v2.zip"));
        Entries(result.OutputPaths[0]).Should().BeEquivalentTo("Photos.v2/", "Photos.v2/a.jpg", "Photos.v2/sub/", "Photos.v2/sub/b.jpg", "Photos.v2/empty/");
        Read(result.OutputPaths[0], "Photos.v2/sub/b.jpg").Should().Be("b");
    }

    [Fact]
    public async Task Zip_of_several_items_is_named_after_the_first_and_numbers_a_name_taken_twice()
    {
        var a = File("one/a.txt", "1");
        var b = File("two/a.txt", "2");

        var result = await Run(Action("action.zip"), a, b);

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "one", "a.zip"));
        Entries(result.OutputPaths[0]).Should().Equal("a.txt", "a (2).txt");
        Read(result.OutputPaths[0], "a (2).txt").Should().Be("2");
    }

    [Fact]
    public async Task Zip_follows_the_conflict_policy()
    {
        var a = File("a.txt");
        File("a.zip", "older");

        var numbered = await Run(Action("action.zip"), a);
        numbered.OutputPaths.Should().Equal(Path.Combine(_dir, "a (1).zip"));

        var zip = Action("action.zip");
        var skip = AppSettings.Default.Request([a], zip.Id) with { Conflict = ConflictPolicy.Skip };
        var skipped = await zip.RunAsync(skip, new InlineProgress(_ => { }), CancellationToken.None);
        skipped.Skipped.Should().BeTrue();
        System.IO.File.ReadAllText(Path.Combine(_dir, "a.zip")).Should().Be("older");
    }

    [Fact]
    public async Task Zip_reports_progress_up_to_the_end()
    {
        var a = File("a.bin", new string('x', 3 << 20));
        var reports = new List<double>();
        var zip = Action("action.zip");

        await zip.RunAsync(AppSettings.Default.Request([a], zip.Id), new InlineProgress(reports.Add), CancellationToken.None);

        reports.Should().HaveCountGreaterThan(1).And.BeInAscendingOrder();
        reports[^1].Should().Be(1);
    }

    [Fact]
    public async Task A_canceled_zip_leaves_nothing_behind()
    {
        var a = File("a.txt");
        var zip = Action("action.zip");

        await zip.Invoking(z => z.RunAsync(AppSettings.Default.Request([a], z.Id), new InlineProgress(_ => { }), new CancellationToken(canceled: true)))
            .Should().ThrowAsync<OperationCanceledException>();

        Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Should().Equal("a.txt");
    }

    private static string[] Entries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return [.. archive.Entries.Select(e => e.FullName)];
    }

    private static string Read(string zip, string entry)
    {
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry(entry)!.Open());
        return reader.ReadToEnd();
    }

    private sealed class FakeDesktop : IDesktop
    {
        public string? Clipboard { get; private set; }

        public List<string> Renamed { get; } = [];

        public Task SetClipboardTextAsync(string text)
        {
            Clipboard = text;
            return Task.CompletedTask;
        }

        public Task StartRenameAsync(string path)
        {
            Renamed.Add(path);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFiles : IFileOperations
    {
        public List<string> Recycled { get; } = [];

        public Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            Recycled.AddRange(paths);
            return Task.CompletedTask;
        }

        public Task MoveAsync(IReadOnlyList<FileMove> moves, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
