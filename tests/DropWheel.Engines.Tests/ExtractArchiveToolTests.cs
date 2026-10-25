using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Engines.Archive;
using DropWheel.Engines.Tools;
using FluentAssertions;

namespace DropWheel.Engines.Tests;

public sealed class ExtractArchiveToolTests : IDisposable
{
    /// <summary>Even seconds: zip stores times in two-second steps.</summary>
    private static readonly DateTime Modified = new(2024, 5, 6, 7, 8, 10, DateTimeKind.Local);

    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-extract-").FullName;
    private readonly ExtractArchiveTool _tool = new(new SharpCompressArchiveConverter());

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <param name="entries">Name → text; a name ending in "/" is a folder.</param>
    private string Zip(string name, params (string Name, string? Text)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, text) in entries)
        {
            var entry = zip.CreateEntry(entryName);
            entry.LastWriteTime = Modified;
            if (text is not null)
            {
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return path;
    }

    private Task<ConversionResult> Run(params string[] sources) =>
        _tool.RunAsync(new(sources, _tool.Id, ""), new InlineProgress(_ => { }), CancellationToken.None);

    /// <summary>Every file and folder under <paramref name="folder"/>, with "/" between names and a folder ending in "/".</summary>
    private static string[] Tree(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(folder, p).Replace('\\', '/') + (Directory.Exists(p) ? "/" : ""))
            .Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_zip_unpacks_into_a_folder_of_its_name()
    {
        var source = Zip("pack.zip", ("报告/数据.txt", "第一行"), ("readme.txt", "hello"), ("空目录/", null));

        var result = await Run(source);

        result.Succeeded.Should().BeTrue(result.Error);
        var folder = Path.Combine(_dir, "pack");
        result.OutputPaths.Should().Equal(folder);
        result.Detail.Should().Be("2 个文件");
        Tree(folder).Should().Equal("readme.txt", "报告/", "报告/数据.txt", "空目录/");
        File.ReadAllText(Path.Combine(folder, "报告", "数据.txt")).Should().Be("第一行");
        File.GetLastWriteTime(Path.Combine(folder, "readme.txt")).Should().Be(Modified);
        Directory.GetFileSystemEntries(_dir).Should().BeEquivalentTo([source, folder], "no temporary folder is left behind");
    }

    [Fact]
    public async Task A_folder_of_the_archives_own_name_is_not_doubled()
    {
        var source = Zip("Photos.zip", ("photos/a.jpg", "a"), ("photos/photos/b.jpg", "b"));

        var result = await Run(source);

        Tree(result.OutputPaths[0]).Should().Equal("a.jpg", "photos/", "photos/b.jpg");
    }

    [Fact]
    public async Task A_folder_of_another_name_is_kept()
    {
        var source = Zip("pack.zip", ("project-v2/a.txt", "a"));

        var result = await Run(source);

        Tree(result.OutputPaths[0]).Should().Equal("project-v2/", "project-v2/a.txt");
    }

    [Fact]
    public async Task A_path_out_of_the_folder_stops_the_job()
    {
        var source = Zip("evil.zip", ("ok.txt", "fine"), ("../escaped.txt", "gotcha"));

        var result = await Run(source);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("包外");
        Directory.GetFileSystemEntries(_dir).Should().Equal(source);
        File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "escaped.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Names_windows_cannot_hold_are_changed()
    {
        var source = Zip("names.zip", ("a:b?.txt", "1"), ("CON.txt", "2"), ("dots...", "3"), ("aux/x.txt", "4"));

        var result = await Run(source);

        Tree(result.OutputPaths[0]).Should().Equal("_CON.txt", "_aux/", "_aux/x.txt", "a_b_.txt", "dots_");
    }

    [Fact]
    public async Task An_existing_folder_gets_a_number()
    {
        var source = Zip("pack.zip", ("a.txt", "a"));
        Directory.CreateDirectory(Path.Combine(_dir, "pack"));

        var result = await Run(source);

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "pack (1)"));
    }

    [Fact]
    public async Task A_tar_gz_unpacks_without_its_links()
    {
        var source = Path.Combine(_dir, "backup.tar.gz");
        using (var file = File.Create(source))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./docs/"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./docs/note.txt") { DataStream = new MemoryStream("note"u8.ToArray()) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "./docs/link") { LinkName = "/etc/passwd" });
        }

        var result = await Run(source);

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "backup"));
        Tree(result.OutputPaths[0]).Should().Equal("docs/", "docs/note.txt");
    }

    [Fact]
    public async Task A_batch_is_all_or_nothing()
    {
        var good = Zip("a.zip", ("a.txt", "a"));
        var bad = Path.Combine(_dir, "b.zip");
        File.WriteAllBytes(bad, "PK\x03\x04 not really"u8.ToArray());

        var result = await Run(good, bad);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().StartWith("b.zip");
        Directory.GetFileSystemEntries(_dir).Should().BeEquivalentTo([good, bad]);
    }

    [Fact]
    public void Only_archives_can_be_unpacked()
    {
        var zip = Zip("a.zip", ("a.txt", "a"));

        _tool.AppliesTo(MediaKind.Archive).Should().BeTrue();
        _tool.AppliesTo(MediaKind.Image).Should().BeFalse();
        _tool.Check(new([zip], _tool.Id, "")).IsAvailable.Should().BeTrue();
        _tool.Check(new([zip, Path.Combine(_dir, "photo.jpg")], _tool.Id, "")).Reason.Should().Be("photo.jpg 不是压缩包");
    }
}
