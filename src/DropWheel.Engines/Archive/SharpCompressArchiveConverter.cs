using System.Globalization;
using System.Text;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using SharpCompress.Writers.Tar;
using SharpCompress.Writers.Zip;

namespace DropWheel.Engines.Archive;

/// <summary>
/// Repacks an archive into another archive format, entry by entry and in process. Paths, contents,
/// modification times and empty folders survive; symbolic links, Unix permissions and comments do not.
/// RAR is read only. Encrypted archives are refused rather than asking for a password.
/// </summary>
public sealed class SharpCompressArchiveConverter : IConverter
{
    internal static readonly string[] Inputs = ["zip", "7z", "tar", "tgz", "rar"];
    private static readonly string[] Outputs = ["zip", "7z", "tar", "tgz"];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Names are written as UTF-8. With <see cref="Encoding.UTF8"/> as the default the zip writer also sets the
    /// UTF-8 flag (bit 11); the library default writes the same bytes unflagged, and Explorer then reads them
    /// in the OEM code page, so Chinese names come out garbled.
    /// </summary>
    private static readonly ArchiveEncoding Utf8Names = new() { Default = Encoding.UTF8 };

    /// <summary>Above this the zip gets Zip64 headers; leaves room for incompressible data growing a little.</summary>
    private const long Zip64Threshold = 3L << 30;

    private readonly TimeProvider _time;
    private readonly ArchiveEncoding _names;

    static SharpCompressArchiveConverter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <param name="legacyNames">
    /// For entry names that are neither flagged nor valid UTF-8, typically zips from older Windows tools.
    /// Defaults to the OEM code page, as Explorer does: GBK on a Chinese system.
    /// </param>
    public SharpCompressArchiveConverter(TimeProvider? time = null, Encoding? legacyNames = null)
    {
        _time = time ?? TimeProvider.System;
        var legacy = legacyNames ?? Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        _names = new() { CustomDecoder = (bytes, index, count, type) => DecodeName(bytes, index, count, type, legacy) };
    }

    public string Id => "sharpcompress";

    public IEnumerable<ConversionEdge> Edges =>
        from i in Inputs
        from o in Outputs
        where i != o
        select new ConversionEdge(i, o);

    public Availability Check(ConversionRequest request) => Availability.Available;

    public QualityEstimate? Estimate(ConversionRequest request)
    {
        var from = FormatRegistry.FromPath(request.SourcePaths[0])?.Id;
        var reasons = new List<string>();
        if (request.TargetFormatId == "tar")
        {
            reasons.Add("tar 不压缩，文件会比原包大");
        }

        if (from is "tar" or "tgz")
        {
            reasons.Add("符号链接和 Unix 权限不保留");
        }

        return reasons.Count > 0 ? new(QualityLevel.High, reasons) : null;
    }

    /// <summary>All or nothing, see <see cref="OutputFiles.ForEachSourceAsync"/>.</summary>
    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Outputs.Contains(request.TargetFormatId))
        {
            return new(false, [], $"不支持输出 {request.TargetFormatId}");
        }

        try
        {
            var written = await OutputFiles.ForEachSourceAsync(request, progress,
                (source, i, within) => Task.Run(() => Convert(request, source, i, within, cancellationToken), cancellationToken), cancellationToken).ConfigureAwait(false);
            return ConversionResult.Written(written);
        }
        catch (InvalidDataException ex)
        {
            return new(false, [], ex.Message);
        }
    }

    private string? Convert(ConversionRequest request, string source, int index, IProgress<double> progress, CancellationToken cancellationToken) =>
        OutputFiles.Write(request, source, index, _time.GetLocalNow(), temp => Repack(source, temp, request.TargetFormatId, progress, cancellationToken));

    private void Repack(string source, string temp, string to, IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var output = File.Create(temp);
        IWriter? writer = null;
        try
        {
            ReadEntries(source, progress, large => writer = OpenWriter(output, to, large), (entry, path, open) => Copy(entry, path, open, writer!), cancellationToken);
        }
        finally
        {
            writer?.Dispose();
        }
    }

    /// <summary>
    /// Reads the archive front to back, handing <paramref name="visit"/> each file and folder with its path and a
    /// way to open its data. Symbolic links and the root itself are left out. <paramref name="opened"/> is told,
    /// before the first entry, whether the archive is big enough to need Zip64 when written again.
    /// </summary>
    /// <exception cref="InvalidDataException">The archive is damaged or has a password.</exception>
    internal void ReadEntries(
        string source,
        IProgress<double> progress,
        Action<bool> opened,
        Action<IEntry, string, Func<Stream>> visit,
        CancellationToken cancellationToken)
    {
        try
        {
            Read(source, progress, opened, visit, cancellationToken);
        }
        catch (CryptographicException)
        {
            throw Encrypted(source);
        }
        catch (SharpCompressException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(source)} 无法读取：{ex.Message}", ex);
        }
    }

    private void Read(string source, IProgress<double> progress, Action<bool> opened, Action<IEntry, string, Func<Stream>> visit, CancellationToken cancellationToken)
    {
        void Visit(IEntry entry, Func<Stream> open, string? unnamed = null)
        {
            if (string.IsNullOrEmpty(entry.LinkTarget) && EntryPath(entry, unnamed) is { } path)
            {
                visit(entry, path, open);
            }
        }

        if (FormatRegistry.FromPath(source)?.Id == "tgz")
        {
            // A compressed tar can only be read front to back, so progress follows the position in the file.
            using var file = File.OpenRead(source);
            using var input = new ObservedStream(file, _ => progress.Report((double)file.Position / Math.Max(1, file.Length)), cancellationToken);
            using var reader = ReaderFactory.OpenReader(input, ReaderOptions.ForExternalStream with { ArchiveEncoding = _names });
            opened(file.Length > Zip64Threshold / 16);

            // A gzip of a single file need not record its name; "notes.txt.gz" then holds "notes.txt".
            var unnamed = reader.Type == ArchiveType.GZip ? FormatRegistry.StemOf(source) : null;
            while (reader.MoveToNextEntry())
            {
                Visit(reader.Entry, () => new ObservedStream(reader.OpenEntryStream(), _ => { }, cancellationToken), unnamed);
            }

            return;
        }

        using var archive = ArchiveFactory.OpenArchive(source, ReaderOptions.ForFilePath with { ArchiveEncoding = _names });
        var entries = archive.Entries.ToList();
        if (entries.Any(e => e.IsEncrypted))
        {
            throw Encrypted(source);
        }

        var total = archive.TotalUncompressedSize;
        var done = 0L;
        Stream Observe(Stream data) => new ObservedStream(data, n => progress.Report(total > 0 ? (double)(done += n) / total : 0), cancellationToken);

        opened(total > Zip64Threshold || entries.Count >= ushort.MaxValue);
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            // Solid data decodes in one pass; opening the entries one by one would restart it for each.
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                Visit(reader.Entry, () => Observe(reader.OpenEntryStream()));
            }
        }
        else
        {
            foreach (var entry in entries)
            {
                Visit(entry, () => Observe(entry.OpenEntryStream()));
            }
        }
    }

    private static IWriter OpenWriter(Stream output, string format, bool zip64) => format switch
    {
        "zip" => new ZipWriter(output, new ZipWriterOptions(CompressionType.Deflate) { ArchiveEncoding = Utf8Names, UseZip64 = zip64 }),
        "7z" => new SevenZipWriter(output, new SevenZipWriterOptions(CompressionType.LZMA2) { CompressHeader = true }),
        "tar" => new TarWriter(output, new TarWriterOptions(CompressionType.None, finalizeArchiveOnClose: true) { ArchiveEncoding = Utf8Names }),
        "tgz" => new TarWriter(output, new TarWriterOptions(CompressionType.GZip, finalizeArchiveOnClose: true) { ArchiveEncoding = Utf8Names }),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    private static void Copy(IEntry entry, string path, Func<Stream> open, IWriter writer)
    {
        if (entry.IsDirectory)
        {
            writer.WriteDirectory(path, entry.LastModifiedTime);
            return;
        }

        using var data = open();
        if (writer is TarWriter tar)
        {
            // A tar header carries the size up front, and entry streams cannot seek to find it.
            tar.Write(path, data, entry.LastModifiedTime, entry.Size);
        }
        else
        {
            writer.Write(path, data, entry.LastModifiedTime);
        }
    }

    /// <summary>Forward slashes, no leading "./" or "/" as tar tools write them; null for the root itself.</summary>
    private static string? EntryPath(IEntry entry, string? unnamed)
    {
        if (string.IsNullOrEmpty(entry.Key))
        {
            return unnamed;
        }

        var path = entry.Key.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        path = path.Trim('/');
        return path is "" or "." ? null : path;
    }

    private static string DecodeName(byte[] bytes, int index, int count, EncodingType type, Encoding legacy)
    {
        if (type == EncodingType.UTF8)
        {
            return Encoding.UTF8.GetString(bytes, index, count);
        }

        try
        {
            return StrictUtf8.GetString(bytes, index, count);
        }
        catch (DecoderFallbackException)
        {
            return legacy.GetString(bytes, index, count);
        }
    }

    private static InvalidDataException Encrypted(string source) => new($"{Path.GetFileName(source)} 有密码保护，暂不支持加密的压缩包");
}
