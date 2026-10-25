using DropWheel.Core.Actions;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Settings;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public sealed class ConflictTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.FromHours(8));
    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-conflicts-").FullName;
    private readonly Bin _files = new();
    private readonly ScriptedPrompt _prompt = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string relative, string text = "x")
    {
        // Full, so "one/a.txt" comes back with the separators the code under test writes on Windows.
        var path = Path.GetFullPath(Path.Combine(_dir, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    private string Folder(string relative) => Directory.CreateDirectory(Path.Combine(_dir, relative)).FullName;

    private string[] Names(string relative = "") =>
        Directory.GetFileSystemEntries(Path.Combine(_dir, relative)).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    private JobConflicts Conflicts(params string[] sources) => new(_files, _prompt, sources, CancellationToken.None);

    private static ConversionRequest Vtt(ConflictPolicy policy, JobConflicts? conflicts, params string[] sources) =>
        new(sources, "vtt", "") { Conflict = policy, Conflicts = conflicts };

    private static Task<string?> Write(ConversionRequest request, int index = 0, string text = "new") =>
        OutputFiles.WriteAsync(request, request.SourcePaths[index], index, Now, temp => System.IO.File.WriteAllTextAsync(temp, text));

    [Fact]
    public async Task Replacing_puts_the_new_output_in_place_and_recycles_the_old_one()
    {
        var source = File("a.srt");
        var old = File("a.vtt", "old");
        var conflicts = Conflicts(source);

        var output = await Write(Vtt(ConflictPolicy.OverwriteToRecycleBin, conflicts, source));

        output.Should().Be(old);
        System.IO.File.ReadAllText(old).Should().Be("new");
        _files.Recycled.Should().Equal(old);
        _files.Contents[old].Should().Be("old");
        conflicts.Replaced.Should().Be(1);
        Names().Should().Equal("a.srt", "a.vtt");
    }

    [Fact]
    public async Task A_failed_write_leaves_the_old_file_where_it_was()
    {
        var source = File("a.srt");
        var old = File("a.vtt", "old");
        var conflicts = Conflicts(source);

        var act = () => OutputFiles.WriteAsync(Vtt(ConflictPolicy.OverwriteToRecycleBin, conflicts, source), source, 0, Now,
            _ => throw new InvalidDataException("bad cue"));

        await act.Should().ThrowAsync<InvalidDataException>();
        System.IO.File.ReadAllText(old).Should().Be("old");
        _files.Recycled.Should().BeEmpty();
        conflicts.Replaced.Should().Be(0);
    }

    [Fact]
    public async Task An_output_this_job_already_wrote_is_numbered_rather_than_replaced()
    {
        var one = File("x/a.srt");
        var two = File("y/a.srt");
        var output = Folder("out");
        var request = Vtt(ConflictPolicy.OverwriteToRecycleBin, Conflicts(one, two), one, two) with { OutputDirectory = output };

        (await Write(request, 0)).Should().Be(Path.Combine(output, "a.vtt"));
        (await Write(request, 1)).Should().Be(Path.Combine(output, "a (1).vtt"));

        _files.Recycled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_and_a_folder_that_holds_one_are_never_replaced_or_asked_about()
    {
        var source = File("in/a.srt");
        var conflicts = Conflicts(source);

        (await conflicts.SettleAsync(source, ConflictPolicy.OverwriteToRecycleBin, more: false)).Should().Be(ConflictPolicy.AutoNumber);
        (await conflicts.SettleAsync(source.ToUpperInvariant(), ConflictPolicy.Ask, more: false)).Should().Be(ConflictPolicy.AutoNumber);
        (await conflicts.SettleAsync(Path.Combine(_dir, "in"), ConflictPolicy.Ask, more: false)).Should().Be(ConflictPolicy.AutoNumber);
        var replace = () => conflicts.ReplaceAsync(Path.Combine(_dir, "in") + Path.DirectorySeparatorChar);

        await replace.Should().ThrowAsync<InvalidOperationException>();
        _prompt.Asked.Should().BeEmpty();
        _files.Recycled.Should().BeEmpty();
    }

    [Fact]
    public async Task Asking_numbers_skips_or_replaces_as_answered_and_a_closed_question_skips()
    {
        var source = File("a.srt");
        var old = File("a.vtt", "old");
        _prompt.Answers.Enqueue(new(ConflictPolicy.AutoNumber));
        _prompt.Answers.Enqueue(new(ConflictPolicy.Skip));
        _prompt.Answers.Enqueue(new(ConflictPolicy.Ask));
        _prompt.Answers.Enqueue(new(ConflictPolicy.OverwriteToRecycleBin));

        (await Write(Vtt(ConflictPolicy.Ask, Conflicts(source), source))).Should().Be(Path.Combine(_dir, "a (1).vtt"));
        (await Write(Vtt(ConflictPolicy.Ask, Conflicts(source), source))).Should().BeNull();
        (await Write(Vtt(ConflictPolicy.Ask, Conflicts(source), source))).Should().BeNull();
        System.IO.File.ReadAllText(old).Should().Be("old");
        (await Write(Vtt(ConflictPolicy.Ask, Conflicts(source), source))).Should().Be(old);

        _prompt.Asked.Should().Equal((old, false), (old, false), (old, false), (old, false));
        _files.Recycled.Should().Equal(old);
        Names().Should().Equal("a (1).vtt", "a.srt", "a.vtt");
    }

    [Fact]
    public async Task An_answer_for_the_rest_is_not_asked_again_and_a_free_name_is_never_asked_about()
    {
        string[] sources = [File("a.srt"), File("b.srt"), File("c.srt"), File("d.srt")];
        File("a.vtt", "old");
        File("b.vtt", "old");
        File("c.vtt", "old");
        _prompt.Answers.Enqueue(new(ConflictPolicy.OverwriteToRecycleBin, ForTheRest: true));
        var conflicts = Conflicts(sources);
        var request = Vtt(ConflictPolicy.Ask, conflicts, sources);

        for (var i = 0; i < sources.Length; i++)
        {
            await Write(request, i);
        }

        _prompt.Asked.Should().Equal((Path.Combine(_dir, "a.vtt"), true));
        conflicts.Replaced.Should().Be(3);
        Directory.GetFiles(_dir, "*.vtt").Select(System.IO.File.ReadAllText).Should().AllBe("new").And.HaveCount(4);
    }

    [Fact]
    public async Task An_old_file_that_will_not_go_to_the_recycle_bin_is_left_alone_and_the_output_fails()
    {
        var keeping = new Bin { Keep = true };
        var source = File("a.srt");
        var old = File("a.vtt", "old");
        var conflicts = new JobConflicts(keeping, null, [source], CancellationToken.None);

        var act = () => Write(Vtt(ConflictPolicy.OverwriteToRecycleBin, conflicts, source));

        (await act.Should().ThrowAsync<IOException>()).WithMessage("a.vtt 没能移到回收站，没有替换它");
        System.IO.File.ReadAllText(old).Should().Be("old");
        conflicts.Replaced.Should().Be(0);
        Names().Should().Equal("a.srt", "a.vtt");
    }

    [Fact]
    public async Task Replacing_or_asking_without_the_job_s_conflicts_is_a_mistake_in_the_caller()
    {
        var source = File("a.srt");

        var act = () => Write(Vtt(ConflictPolicy.Ask, null, source));

        await act.Should().ThrowAsync<InvalidOperationException>();
        Names().Should().Equal("a.srt");
    }

    [Fact]
    public async Task Without_a_prompt_a_taken_name_is_numbered()
    {
        var source = File("a.srt");
        File("a.vtt", "old");

        var output = await Write(Vtt(ConflictPolicy.Ask, new JobConflicts(_files, null, [source], CancellationToken.None), source));

        output.Should().Be(Path.Combine(_dir, "a (1).vtt"));
    }

    [Fact]
    public void A_derived_request_keeps_the_job_s_conflicts()
    {
        var conflicts = Conflicts("/in/a.jpg");
        var request = new ConversionRequest(["/in/a.jpg"], "png", "") { Conflicts = conflicts };

        (request with { SourcePaths = ["/in/b.jpg"], TargetFormatId = "pdf", Naming = "{name}-合并" }).Conflicts.Should().BeSameAs(conflicts);
    }

    [Fact]
    public async Task The_queue_asks_through_its_prompt_and_counts_what_a_job_replaced()
    {
        var jpg = File("a.jpg");
        var old = File("a.png", "old");
        var graph = new ConversionGraph();
        graph.Register(new TestConverter([new("jpg", "png")], async (r, _, _) =>
            ConversionResult.Written([(await OutputFiles.WriteAsync(r, r.SourcePaths[0], 0, Now, temp => System.IO.File.WriteAllTextAsync(temp, "new")))!])));
        await using var queue = new JobQueue(graph, new UndoLedger(_files), prompt: _prompt);
        _prompt.Answers.Enqueue(new(ConflictPolicy.OverwriteToRecycleBin));

        var job = queue.Enqueue(new([jpg], "png", "") { Conflict = ConflictPolicy.Ask }, "a.jpg → PNG");
        await queue.WhenIdleAsync();

        job.State.Should().Be(JobState.Done);
        job.Replaced.Should().Be(1);
        job.Outputs.Should().Equal(old);
        job.Request.Conflicts.Should().BeNull();
        _prompt.Asked.Should().Equal((old, false));

        (await queue.UndoAsync(job.Id)).Should().BeTrue();
        _files.Recycled.Should().Equal(old, old);
        _files.Contents[old].Should().Be("new");
    }

    [Fact]
    public async Task A_job_that_fails_after_replacing_still_says_how_many_it_replaced()
    {
        var jpg = File("a.jpg");
        File("a.png", "old");
        var graph = new ConversionGraph();
        graph.Register(new TestConverter([new("jpg", "png")], async (r, _, _) =>
        {
            await OutputFiles.WriteAsync(r, r.SourcePaths[0], 0, Now, temp => System.IO.File.WriteAllTextAsync(temp, "new"));
            throw new IOException("disk full");
        }));
        await using var queue = new JobQueue(graph, new UndoLedger(_files));

        var job = queue.Enqueue(new([jpg], "png", "") { Conflict = ConflictPolicy.OverwriteToRecycleBin }, "a.jpg → PNG");
        await queue.WhenIdleAsync();

        job.State.Should().Be(JobState.Failed);
        job.Error.Should().Be("disk full");
        job.Replaced.Should().Be(1);
    }

    [Fact]
    public async Task Moving_onto_a_taken_name_recycles_it_first_and_numbers_a_second_item_of_that_name()
    {
        var one = File("one/a.txt", "1");
        var two = File("two/a.txt", "2");
        var dest = Folder("dest");
        var taken = File("dest/a.txt", "older");
        var move = FileActions.Create(_files, new PickingDesktop(dest)).Single(a => a.Id == "action.move-to");
        var conflicts = Conflicts(one, two);

        var result = await move.RunAsync(
            AppSettings.Default.Request([one, two], move.Id) with { Conflict = ConflictPolicy.OverwriteToRecycleBin, Conflicts = conflicts },
            new InlineProgress(_ => { }), CancellationToken.None);

        _files.Log.Should().Equal("recycle a.txt", $"move {one} → {taken}", $"move {two} → {Path.Combine(dest, "a (1).txt")}");
        _files.Contents[taken].Should().Be("older");
        conflicts.Replaced.Should().Be(1);
        result.Undo.Should().BeOfType<UndoPlan.MoveBack>();
    }

    [Fact]
    public async Task Copying_into_the_item_s_own_folder_numbers_the_copy_without_asking()
    {
        var a = File("a.txt");
        var copy = FileActions.Create(_files, new PickingDesktop(_dir)).Single(a => a.Id == "action.copy-to");

        await copy.RunAsync(
            AppSettings.Default.Request([a], copy.Id) with { Conflict = ConflictPolicy.Ask, Conflicts = Conflicts(a) },
            new InlineProgress(_ => { }), CancellationToken.None);

        _prompt.Asked.Should().BeEmpty();
        _files.Log.Should().Equal($"copy {a} → {Path.Combine(_dir, "a (1).txt")}");
    }

    [Fact]
    public async Task A_zip_replaces_one_of_the_same_name()
    {
        var a = File("a.txt");
        var old = File("a.zip", "older");
        var zip = FileActions.Create(_files, new PickingDesktop(_dir)).Single(a => a.Id == "action.zip");
        var conflicts = Conflicts(a);

        var result = await zip.RunAsync(
            AppSettings.Default.Request([a], zip.Id) with { Conflict = ConflictPolicy.OverwriteToRecycleBin, Conflicts = conflicts },
            new InlineProgress(_ => { }), CancellationToken.None);

        result.OutputPaths.Should().Equal(old);
        _files.Contents[old].Should().Be("older");
        conflicts.Replaced.Should().Be(1);
    }

    /// <summary>Recycles by deleting, keeping what each file held; with <see cref="Keep"/>, leaves everything where it is.</summary>
    private sealed class Bin : IFileOperations
    {
        public bool Keep { get; init; }

        public List<string> Recycled { get; } = [];

        public Dictionary<string, string> Contents { get; } = [];

        public List<string> Log { get; } = [];

        public Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            foreach (var path in paths)
            {
                Recycled.Add(path);
                Log.Add($"recycle {Path.GetFileName(path)}");
                if (Keep)
                {
                    continue;
                }

                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    Contents[path] = System.IO.File.ReadAllText(path);
                    System.IO.File.Delete(path);
                }
            }

            return Task.CompletedTask;
        }

        public Task MoveAsync(IReadOnlyList<FileMove> moves, CancellationToken cancellationToken = default)
        {
            Log.AddRange(moves.Select(m => $"move {m.From} → {m.To}"));
            return Task.CompletedTask;
        }

        public Task CopyAsync(IReadOnlyList<FileMove> copies, CancellationToken cancellationToken = default)
        {
            Log.AddRange(copies.Select(m => $"copy {m.From} → {m.To}"));
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedPrompt : IConflictPrompt
    {
        public Queue<ConflictAnswer> Answers { get; } = new();

        public List<(string Target, bool More)> Asked { get; } = [];

        public Task<ConflictAnswer> AskAsync(string target, bool more, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add((target, more));
                return Task.FromResult(Answers.Dequeue());
            }
        }
    }

    private sealed class PickingDesktop(string folder) : IDesktop
    {
        public Task SetClipboardTextAsync(string text) => Task.CompletedTask;

        public Task StartRenameAsync(string path) => Task.CompletedTask;

        public Task<string?> PickFolderAsync(FolderPrompt prompt, CancellationToken cancellationToken) => Task.FromResult<string?>(folder);
    }
}
