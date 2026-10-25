using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Engines.Archive;
using FluentAssertions;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace DropWheel.Engines.Tests;

public sealed class ArchiveConverterTests : IDisposable
{
    /// <summary>`zip -X -P pw` of a 7-byte s.txt: traditional PKWARE encryption, no extra fields.</summary>
    private const string EncryptedZip = "UEsDBAoACQAAAM8jWV2MsuviEwAAAAcAAAAFAAAAcy50eHSCFC90ajj/ODyNOFwQIPBPb1mfUEsHCIyy6+ITAAAABwAAAFBLAQIeAwoACQAAAM8jWV2MsuviEwAAAAcAAAAFAAAAAAAAAAEAAACkgQAAAABzLnR4dFBLBQYAAAAAAQABADMAAABGAAAAAAA=";

    /// <summary>Even seconds: zip stores times in two-second steps.</summary>
    private static readonly DateTime Modified = new(2024, 5, 6, 7, 8, 10, DateTimeKind.Local);

    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-archive-").FullName;
    private readonly SharpCompressArchiveConverter _converter = new(legacyNames: Gbk);

    private static Encoding Gbk
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Zip(string name, Encoding? names = null)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create, names);
        foreach (var (entryName, text) in new[] { ("报告/数据.txt", "第一行\n第二行"), ("readme.txt", "hello"), ("空目录/", null) })
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

    private async Task<string> Convert(string source, string target)
    {
        var result = await _converter.RunAsync(new([source], target, ""), new InlineProgress(_ => { }), CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.Error);
        return result.OutputPaths.Single();
    }

    /// <summary>Path → (text, or null for a folder; modification time) as SharpCompress reads it back.</summary>
    private static Dictionary<string, (string? Text, DateTime? Modified)> Read(string path)
    {
        var entries = new Dictionary<string, (string?, DateTime?)>();
        void Add(SharpCompress.Common.IEntry e, Func<Stream> open)
        {
            string? text = null;
            if (!e.IsDirectory)
            {
                using var s = open();
                text = new StreamReader(s, Encoding.UTF8).ReadToEnd();
            }

            entries.Add(e.Key!.TrimEnd('/'), (text, e.LastModifiedTime?.ToLocalTime()));
        }

        if (path.EndsWith(".tgz", StringComparison.Ordinal))
        {
            using var file = File.OpenRead(path);
            using var reader = ReaderFactory.OpenReader(file, ReaderOptions.ForExternalStream);
            while (reader.MoveToNextEntry())
            {
                Add(reader.Entry, reader.OpenEntryStream);
            }
        }
        else
        {
            using var archive = ArchiveFactory.OpenArchive(path, ReaderOptions.ForFilePath);
            foreach (var e in archive.Entries)
            {
                Add(e, e.OpenEntryStream);
            }
        }

        return entries;
    }

    [Fact]
    public void Every_input_repacks_to_every_other_output_but_rar()
    {
        _converter.Edges.Should().HaveCount(16);
        _converter.Edges.Should().Contain(new ConversionEdge("rar", "zip")).And.Contain(new ConversionEdge("tgz", "7z"));
        _converter.Edges.Should().NotContain(e => e.ToFormatId == "rar");
    }

    [Fact]
    public async Task Zip_survives_a_trip_through_every_output()
    {
        var zip = Zip("bundle.zip");

        var sevenZip = await Convert(zip, "7z");
        var tgz = await Convert(sevenZip, "tgz");
        var tar = await Convert(tgz, "tar");
        var back = await Convert(tar, "zip");

        Path.GetFileName(back).Should().Be("bundle (1).zip");
        foreach (var path in new[] { sevenZip, tgz, tar, back })
        {
            var entries = Read(path);
            entries.Keys.Should().BeEquivalentTo(["报告/数据.txt", "readme.txt", "空目录"], path);
            entries["报告/数据.txt"].Should().Be(("第一行\n第二行", Modified), path);
            entries["readme.txt"].Text.Should().Be("hello");
            entries["空目录"].Text.Should().BeNull("an empty folder stays a folder");
        }
    }

    [Fact]
    public async Task Zip_output_flags_its_names_as_utf8()
    {
        var output = await Convert(await Convert(Zip("bundle.zip"), "7z"), "zip");

        var bytes = File.ReadAllBytes(output);
        (BitConverter.ToUInt16(bytes, 6) & 0x0800).Should().NotBe(0, "Explorer reads unflagged names in the OEM code page");
        using var zip = ZipFile.OpenRead(output);
        zip.Entries.Select(e => e.FullName).Should().Contain("报告/数据.txt");
    }

    [Fact]
    public async Task Unflagged_names_fall_back_to_the_legacy_code_page()
    {
        var gbk = Zip("legacy.zip", Gbk);
        (BitConverter.ToUInt16(File.ReadAllBytes(gbk), 6) & 0x0800).Should().Be(0, "the sample has to be a legacy zip");

        Read(await Convert(gbk, "7z")).Keys.Should().Contain("报告/数据.txt");
    }

    [Fact]
    public async Task Tar_paths_lose_their_dot_prefix_and_links_are_skipped()
    {
        var tar = Path.Combine(_dir, "unix.tar");
        using (var file = File.Create(tar))
        using (var writer = new TarWriter(file, TarEntryFormat.Ustar))
        {
            writer.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "./"));
            writer.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "./docs/"));
            writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "./docs/a.txt") { DataStream = new MemoryStream("abc"u8.ToArray()) });
            writer.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, "./latest") { LinkName = "docs/a.txt" });
        }

        var entries = Read(await Convert(tar, "zip"));

        entries.Keys.Should().BeEquivalentTo(["docs", "docs/a.txt"]);
        entries["docs/a.txt"].Text.Should().Be("abc");
    }

    [Fact]
    public async Task Single_file_gzip_becomes_an_archive_holding_that_file()
    {
        var gz = Path.Combine(_dir, "notes.txt.gz");
        using (var file = File.Create(gz))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        {
            gzip.Write("line"u8);
        }

        var output = await Convert(gz, "zip");

        Path.GetFileName(output).Should().Be("notes.txt.zip");
        Read(output).Should().ContainKey("notes.txt").WhoseValue.Text.Should().Be("line");
    }

    [Fact]
    public async Task Encrypted_archive_fails_with_a_reason_and_leaves_nothing()
    {
        var zip = Path.Combine(_dir, "secret.zip");
        File.WriteAllBytes(zip, System.Convert.FromBase64String(EncryptedZip));

        var result = await _converter.RunAsync(new([zip], "7z", ""), new InlineProgress(_ => { }), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("secret.zip 有密码保护，暂不支持加密的压缩包");
        Directory.GetFiles(_dir).Should().Equal(zip);
    }

    [Fact]
    public async Task Damaged_archive_fails_the_whole_batch()
    {
        var good = Zip("good.zip");
        var broken = Path.Combine(_dir, "broken.zip");
        File.WriteAllBytes(broken, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 31))]);

        var result = await _converter.RunAsync(new([good, broken], "tar", ""), new InlineProgress(_ => { }), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().StartWith("broken.zip 无法读取");
        Directory.GetFiles(_dir).Should().BeEquivalentTo([good, broken], "the tar already written for good.zip is rolled back");
    }

    [Theory]
    [InlineData("a.zip", "7z", null)]
    [InlineData("a.zip", "tar", "tar 不压缩，文件会比原包大")]
    [InlineData("a.tar.gz", "zip", "符号链接和 Unix 权限不保留")]
    [InlineData("a.rar", "tgz", null)]
    public void Estimate_names_what_is_lost(string source, string target, string? reason)
    {
        var estimate = _converter.Estimate(new([source], target, ""));

        if (reason is null)
        {
            estimate.Should().BeNull();
        }
        else
        {
            estimate!.Reasons.Should().Equal(reason);
        }
    }
}
