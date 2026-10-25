using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Archive;
using SharpCompress.Common;

namespace DropWheel.Engines.Tools;

/// <summary>
/// Unpacks each archive into a folder named after it, where outputs go and following the conflict policy.
/// An archive that holds a single folder of its own name does not get a second one around it. A path that
/// would land outside the folder stops the job; names Windows cannot hold are changed; symbolic links are
/// left out. Undo recycles the folder.
/// </summary>
public sealed class ExtractArchiveTool(SharpCompressArchiveConverter archives, TimeProvider? time = null) : IWheelOperation
{
    private const int BufferSize = 1 << 20;

    private static readonly HashSet<string> Reserved = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })],
        StringComparer.OrdinalIgnoreCase);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "tool.extract";

    public WheelMode Mode => WheelMode.Tools;

    public string Label => "解压";

    public string Hint => "到文件夹";

    public bool AppliesTo(MediaKind kind) => kind == MediaKind.Archive;

    public Availability Check(ConversionRequest request)
    {
        foreach (var path in request.SourcePaths)
        {
            if (FormatRegistry.FromPath(path) is not { } format || !SharpCompressArchiveConverter.Inputs.Contains(format.Id))
            {
                return Availability.Unavailable($"{Path.GetFileName(path)} 不是压缩包");
            }

            if (!File.Exists(path))
            {
                return Availability.Unavailable($"找不到 {Path.GetFileName(path)}");
            }
        }

        return Availability.Available;
    }

    /// <summary>All or nothing, see <see cref="OutputFiles.ForEachSourceAsync"/>.</summary>
    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var now = _time.GetLocalNow();
        var files = 0;
        try
        {
            var written = await OutputFiles.ForEachSourceAsync(request, progress, (source, i, within) =>
            {
                var plan = request with { TargetFormatId = FormatRegistry.FromPath(source)!.Id, Naming = NamingTemplate.Default };
                return OutputFiles.WriteFolderAsync(plan, source, i, now, folder => Task.Run(() =>
                {
                    archives.ReadEntries(source, within, cancellationToken, _ => { }, (entry, path, open) =>
                    {
                        if (Unpack(folder, entry, path, open))
                        {
                            files++;
                        }
                    });
                    Flatten(folder, FormatRegistry.StemOf(source));
                }, cancellationToken));
            }, cancellationToken).ConfigureAwait(false);
            return ConversionResult.Written(written) with { Detail = written.Count > 0 ? $"{files} 个文件" : null };
        }
        catch (InvalidDataException ex)
        {
            return new(false, [], ex.Message);
        }
    }

    /// <returns>Whether it wrote a file, rather than a folder.</returns>
    private static bool Unpack(string root, IEntry entry, string path, Func<Stream> open)
    {
        var target = Inside(root, path);
        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(target);
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (var data = open())
        using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
        {
            data.CopyTo(file, BufferSize);
        }

        if (entry.LastModifiedTime is { } modified)
        {
            File.SetLastWriteTime(target, modified);
        }

        return true;
    }

    /// <summary>
    /// Where an entry goes under <paramref name="root"/>, its names made safe for Windows: characters it does not
    /// allow become "_", as do trailing dots and spaces, which Windows would drop, and a device name such as
    /// "CON" gets a "_" in front.
    /// </summary>
    /// <exception cref="InvalidDataException">The path climbs out of the root ("zip slip").</exception>
    internal static string Inside(string root, string path)
    {
        var parts = new List<string> { root };
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                throw new InvalidDataException($"压缩包里有指向包外的路径（{path}），已停止解压");
            }

            var name = string.Concat(part.Select(c => c < ' ' || "<>:\"\\|?*".Contains(c) ? '_' : c));
            var kept = name.TrimEnd('.', ' ');
            if (kept.Length < name.Length)
            {
                name = kept + "_";
            }

            if (Reserved.Contains(name.Split('.')[0].TrimEnd()))
            {
                name = "_" + name;
            }

            parts.Add(name);
        }

        var full = Path.GetFullPath(Path.Combine([.. parts]));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"压缩包里有指向包外的路径（{path}），已停止解压");
        }

        return full;
    }

    /// <summary>"photos.zip" holding just "photos/" unpacks to "photos", not "photos/photos".</summary>
    private static void Flatten(string folder, string stem)
    {
        if (Directory.GetFileSystemEntries(folder) is not [var only]
            || !Directory.Exists(only)
            || !string.Equals(Path.GetFileName(only), stem, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Out of the way first: it may hold an item of its own name.
        var inner = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        Directory.Move(only, inner);
        foreach (var item in Directory.GetFileSystemEntries(inner))
        {
            var to = Path.Combine(folder, Path.GetFileName(item));
            if (Directory.Exists(item))
            {
                Directory.Move(item, to);
            }
            else
            {
                File.Move(item, to);
            }
        }

        Directory.Delete(inner);
    }
}
