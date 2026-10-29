using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public sealed class OutputFilesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.FromHours(8));
    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-outputs-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Source(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "source");
        return path;
    }

    private string[] Names() => Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Fact]
    public async Task Writes_through_a_temporary_name_that_does_not_carry_the_extension()
    {
        var source = Source("a.srt");
        string? seen = null;

        var output = await OutputFiles.WriteAsync(new([source], "vtt", ""), source, 0, Now, temp =>
        {
            seen = temp;
            return File.WriteAllTextAsync(temp, "WEBVTT");
        });

        output.Should().Be(Path.Combine(_dir, "a.vtt"));
        seen.Should().EndWith(OutputFiles.TempSuffix).And.StartWith(Path.Combine(_dir, ".a.vtt."));
        Names().Should().Equal("a.srt", "a.vtt");
    }

    [Fact]
    public async Task An_output_folder_that_does_not_exist_yet_is_created()
    {
        var source = Source("a.srt");
        var folder = Path.Combine(_dir, "out", "subtitles");

        var output = await OutputFiles.WriteAsync(new([source], "vtt", folder), source, 0, Now, temp => File.WriteAllTextAsync(temp, "WEBVTT"));

        output.Should().Be(Path.Combine(folder, "a.vtt"));
        File.ReadAllText(output!).Should().Be("WEBVTT");
    }

    [Fact]
    public void A_file_that_appears_while_writing_is_not_overwritten()
    {
        var source = Source("a.srt");
        var target = Path.Combine(_dir, "a.vtt");

        var output = OutputFiles.Write(new([source], "vtt", ""), source, 0, Now, temp =>
        {
            File.WriteAllText(temp, "ours");
            File.WriteAllText(target, "theirs");
        });

        output.Should().Be(Path.Combine(_dir, "a (1).vtt"));
        File.ReadAllText(target).Should().Be("theirs");
        File.ReadAllText(output!).Should().Be("ours");
    }

    [Fact]
    public void Skip_writes_nothing_when_the_target_exists()
    {
        var source = Source("a.srt");
        File.WriteAllText(Path.Combine(_dir, "a.vtt"), "old");
        var called = false;

        var output = OutputFiles.Write(new([source], "vtt", "") { Conflict = ConflictPolicy.Skip }, source, 0, Now, _ => called = true);

        output.Should().BeNull();
        called.Should().BeFalse();
    }

    [Fact]
    public void A_failing_writer_leaves_no_temporary_file()
    {
        var source = Source("a.srt");

        var act = () => OutputFiles.Write(new([source], "vtt", ""), source, 0, Now, temp =>
        {
            File.WriteAllText(temp, "half");
            throw new InvalidDataException("bad cue");
        });

        act.Should().Throw<InvalidDataException>();
        Names().Should().Equal("a.srt");
    }

    [Fact]
    public async Task A_failing_source_deletes_the_outputs_of_the_others_and_rethrows()
    {
        var sources = new[] { Source("a.srt"), Source("b.srt"), Source("c.srt") };
        var progress = new List<double>();

        var request = new ConversionRequest(sources, "vtt", "");
        var act = () => OutputFiles.ForEachSourceAsync(request, new InlineProgress(progress.Add), (source, i, _) =>
            i == 2
                ? throw new InvalidDataException("bad cue")
                : Task.FromResult(OutputFiles.Write(request, source, i, Now, temp => File.WriteAllText(temp, "x"))),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        Names().Should().Equal("a.srt", "b.srt", "c.srt");
        progress.Should().Equal(1 / 3.0, 2 / 3.0);
    }

    [Fact]
    public async Task A_folder_is_filled_under_a_temporary_name_and_renamed_into_place()
    {
        var source = Source("report.pdf");
        string? seen = null;

        var output = await OutputFiles.WriteFolderAsync(new([source], "png", ""), source, 0, Now, temp =>
        {
            seen = temp;
            Directory.GetFileSystemEntries(temp).Should().BeEmpty();
            return File.WriteAllTextAsync(Path.Combine(temp, "report-1.png"), "page");
        });

        output.Should().Be(Path.Combine(_dir, "report"));
        seen.Should().EndWith(OutputFiles.TempSuffix);
        Names().Should().Equal("report", "report.pdf");
        Directory.GetFiles(output!).Select(Path.GetFileName).Should().Equal("report-1.png");
    }

    [Fact]
    public async Task A_folder_takes_the_next_free_name_when_a_file_or_folder_has_its_name()
    {
        var source = Source("report.pdf");
        Directory.CreateDirectory(Path.Combine(_dir, "report"));
        File.WriteAllText(Path.Combine(_dir, "report (1)"), "a file without an extension");

        var output = await OutputFiles.WriteFolderAsync(new([source], "png", ""), source, 0, Now,
            temp => File.WriteAllTextAsync(Path.Combine(temp, "p.png"), "page"));

        output.Should().Be(Path.Combine(_dir, "report (2)"));
    }

    [Fact]
    public async Task A_failing_source_also_deletes_folders_written_before_it()
    {
        var sources = new[] { Source("a.pdf"), Source("b.pdf") };
        var request = new ConversionRequest(sources, "png", "");

        var act = () => OutputFiles.ForEachSourceAsync(request, new InlineProgress(_ => { }), (source, i, _) =>
            i == 1
                ? throw new InvalidDataException("damaged")
                : OutputFiles.WriteFolderAsync(request, source, i, Now, temp => File.WriteAllTextAsync(Path.Combine(temp, "a-1.png"), "page")),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        Names().Should().Equal("a.pdf", "b.pdf");
    }

    [Fact]
    public async Task A_failing_folder_writer_leaves_nothing_behind()
    {
        var source = Source("report.pdf");

        var act = () => OutputFiles.WriteFolderAsync(new([source], "png", ""), source, 0, Now, async temp =>
        {
            await File.WriteAllTextAsync(Path.Combine(temp, "report-1.png"), "page");
            throw new InvalidDataException("page 2 is damaged");
        });

        await act.Should().ThrowAsync<InvalidDataException>();
        Names().Should().Equal("report.pdf");
    }

    [Fact]
    public async Task Rollback_preserves_a_replacement_with_the_same_size_and_timestamp()
    {
        var sources = new[] { Source("a.srt"), Source("b.srt") };
        var request = new ConversionRequest(sources, "vtt", "");
        var target = Path.Combine(_dir, "a.vtt");
        var act = () => OutputFiles.ForEachSourceAsync(request, new InlineProgress(_ => { }), (source, i, _) =>
        {
            if (i == 0)
            {
                return Task.FromResult(OutputFiles.Write(request, source, i, Now, temp => File.WriteAllText(temp, "ours")));
            }

            var modified = File.GetLastWriteTimeUtc(target);
            File.WriteAllText(target, "user");
            File.SetLastWriteTimeUtc(target, modified);
            throw new InvalidDataException("second source failed");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        File.ReadAllText(target).Should().Be("user");
    }

    [Fact]
    public async Task Rollback_preserves_new_files_added_to_an_output_folder()
    {
        var request = new ConversionRequest([Source("a.pdf"), Source("b.pdf")], "png", "");
        var target = Path.Combine(_dir, "a");
        var act = () => OutputFiles.ForEachSourceAsync(request, new InlineProgress(_ => { }), (source, i, _) =>
        {
            if (i == 0)
            {
                return OutputFiles.WriteFolderAsync(request, source, i, Now, temp => File.WriteAllTextAsync(Path.Combine(temp, "page.png"), "ours"));
            }

            File.WriteAllText(Path.Combine(target, "notes.txt"), "user");
            throw new InvalidDataException("second source failed");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        File.ReadAllText(Path.Combine(target, "notes.txt")).Should().Be("user");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_after_writing_does_not_commit_a_file_or_folder(bool folder)
    {
        using var cancel = new CancellationTokenSource();
        var source = Source("a.pdf");
        var request = new ConversionRequest([source], "png", "");
        Task Write(string temp)
        {
            File.WriteAllText(folder ? Path.Combine(temp, "page.png") : temp, "ours");
            cancel.Cancel();
            return Task.CompletedTask;
        }

        var act = () => folder
            ? OutputFiles.WriteFolderAsync(request, source, 0, Now, Write, cancel.Token)
            : OutputFiles.WriteAsync(request, source, 0, Now, Write, cancellationToken: cancel.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        Names().Should().Equal("a.pdf");
    }

    [Fact]
    public async Task Cancel_on_the_last_progress_report_rolls_back_the_batch()
    {
        using var cancel = new CancellationTokenSource();
        var source = Source("a.srt");
        var request = new ConversionRequest([source], "vtt", "");
        var act = () => OutputFiles.ForEachSourceAsync(request, new InlineProgress(_ => cancel.Cancel()),
            (path, i, _) => OutputFiles.WriteAsync(request, path, i, Now, temp => File.WriteAllTextAsync(temp, "ours"), cancellationToken: cancel.Token), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Names().Should().Equal("a.srt");
    }

    [Fact]
    public async Task Progress_within_a_source_is_scaled_into_the_batch()
    {
        var sources = new[] { Source("a.srt"), Source("b.srt") };
        var progress = new List<double>();

        var written = await OutputFiles.ForEachSourceAsync(new(sources, "vtt", ""), new InlineProgress(progress.Add), (source, i, within) =>
        {
            within.Report(0.5);
            return Task.FromResult(OutputFiles.Write(new(sources, "vtt", ""), source, i, Now, temp => File.WriteAllText(temp, "x")));
        }, CancellationToken.None);

        written.Select(Path.GetFileName).Should().Equal("a.vtt", "b.vtt");
        progress.Should().Equal(0.25, 0.5, 0.75, 1.0);
    }
}
