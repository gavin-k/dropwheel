using System.IO.Compression;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Actions;

/// <summary>
/// Packs the items into one zip named after the first, as Explorer's 压缩为 ZIP 文件 does: "report.docx"
/// becomes "report.zip" and a folder keeps its whole name. It goes where outputs go and follows the
/// conflict policy; the naming template is for conversions and does not apply. Folders are packed under
/// their own name with empty subfolders kept. Undo recycles the zip.
/// </summary>
public sealed class CompressZipAction(TimeProvider? time = null) : IWheelOperation
{
    private const int BufferSize = 1 << 20;

    /// <summary>The years a zip entry's DOS timestamp can hold.</summary>
    private static readonly DateTime Earliest = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
    private static readonly DateTime Latest = new(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "action.zip";

    public WheelMode Mode => WheelMode.Actions;

    public string Label => L.T("压缩");

    public string Hint => "ZIP";

    public FileAction? Action => FileAction.CompressZip;

    public Availability Check(ConversionRequest request) => FileActions.CheckItems(request.SourcePaths);

    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var first = request.SourcePaths[0];

        // The name goes in as a literal template. A folder name that happens to contain a token such as
        // {date} would be expanded; that is rare enough to accept.
        var plan = request with { SourcePaths = [first], TargetFormatId = "zip", Naming = NameOf(first) };
        var written = await OutputFiles.WriteAsync(plan, first, 0, _time.GetLocalNow(),
            temp => Task.Run(() => Pack(request.SourcePaths, temp, progress, cancellationToken), cancellationToken)).ConfigureAwait(false);
        return ConversionResult.Written(written is null ? [] : [written]);
    }

    /// <summary>The zip's base name: a folder's whole name, a file's name without its extension.</summary>
    public static string NameOf(string path) =>
        Directory.Exists(path) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : FormatRegistry.StemOf(path);

    private static void Pack(IReadOnlyList<string> items, string zipPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var entries = Entries(items, zipPath);
        var total = Math.Max(1, entries.Sum(e => e.Length));
        long done = 0;
        var buffer = new byte[BufferSize];

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var e in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (e.Length < 0)
            {
                zip.CreateEntry(e.Name + "/").LastWriteTime = Clamp(Directory.GetLastWriteTime(e.Path));
                continue;
            }

            var entry = zip.CreateEntry(e.Name, CompressionLevel.Optimal);
            entry.LastWriteTime = Clamp(File.GetLastWriteTime(e.Path));
            using var source = new FileStream(e.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
            using var target = entry.Open();
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                target.Write(buffer, 0, read);
                done += read;
                progress.Report((double)done / total);
            }
        }
    }

    /// <summary>Every file and folder to pack, folders first within each item; a folder's Length is -1.</summary>
    private static List<(string Path, string Name, long Length)> Entries(IReadOnlyList<string> items, string zipPath)
    {
        var entries = new List<(string, string, long)>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(L.T("不能压缩符号链接或联接：{0}", Path.GetFileName(item)));
            }

            var name = Unique(Path.GetFileName(Path.TrimEndingDirectorySeparator(item)), taken);
            if (!Directory.Exists(item))
            {
                entries.Add((item, name, new FileInfo(item).Length));
                continue;
            }

            entries.Add((item, name, -1));

            // Keep hidden files, but never follow links outside the selected tree.
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var info in new DirectoryInfo(item).EnumerateFileSystemInfos("*", options))
            {
                // The zip being written, when outputs go into the folder being packed.
                if (info.FullName.EndsWith(OutputFiles.TempSuffix, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(info.FullName, zipPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var entryName = name + "/" + Path.GetRelativePath(item, info.FullName).Replace('\\', '/');
                entries.Add((info.FullName, entryName, info is FileInfo f ? f.Length : -1));
            }
        }

        return entries;
    }

    /// <summary>Two items with one name, from search results say, become "a.txt" and "a (2).txt".</summary>
    private static string Unique(string name, HashSet<string> taken)
    {
        var candidate = name;
        for (var i = 2; !taken.Add(candidate); i++)
        {
            candidate = $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}";
        }

        return candidate;
    }

    private static DateTime Clamp(DateTime t) => t < Earliest ? Earliest : t > Latest ? Latest : t;
}
