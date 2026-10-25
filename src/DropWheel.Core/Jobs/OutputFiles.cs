using DropWheel.Core.Conversion;

namespace DropWheel.Core.Jobs;

/// <summary>
/// How every engine writes its outputs. Each output is rendered into a temporary file (or folder) next to its
/// target and moved into place without overwriting, and a batch is all or nothing.
///
/// The temporary name ends in ".dropwheel-tmp" rather than the real extension, so Explorer does not start
/// a thumbnail handler on a half-written file and lock it before the move. Writers therefore have to name
/// the format explicitly (a Magick format, ffmpeg's -f) instead of letting a library guess it from the name.
/// </summary>
public static class OutputFiles
{
    public const string TempSuffix = ".dropwheel-tmp";

    /// <summary>
    /// Converts each source in turn with <paramref name="convert"/> (source, index, progress within that
    /// source) and reports overall progress. If a source fails or the job is canceled, outputs this run
    /// already wrote are deleted, since they did not exist before it, and the exception propagates.
    /// </summary>
    /// <returns>The written paths; sources the conflict policy skipped have none.</returns>
    public static async Task<IReadOnlyList<string>> ForEachSourceAsync(
        ConversionRequest request,
        IProgress<double> progress,
        Func<string, int, IProgress<double>, Task<string?>> convert,
        CancellationToken cancellationToken)
    {
        var written = new List<string>();
        var count = request.SourcePaths.Count;
        try
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var done = i;
                var within = new InlineProgress(p => progress.Report((done + Math.Clamp(p, 0, 1)) / count));
                if (await convert(request.SourcePaths[i], i, within).ConfigureAwait(false) is { } output)
                {
                    written.Add(output);
                }

                progress.Report((i + 1.0) / count);
            }

            return written;
        }
        catch
        {
            foreach (var path in written)
            {
                TryDelete(path);
            }

            throw;
        }
    }

    /// <summary>
    /// Plans where the output of <paramref name="source"/> goes, has <paramref name="write"/> render it
    /// into a temporary path, and moves it into place. A file that appeared at the target in the meantime
    /// is never overwritten: AutoNumber picks the next free name, any other policy fails.
    /// </summary>
    /// <param name="index">Position of the source in the request; numbers the outputs of a multi-file request.</param>
    /// <param name="width">For the {w} placeholder, when the engine knows it before writing.</param>
    /// <param name="height">For the {h} placeholder.</param>
    /// <returns>The written path, or null when the conflict policy skipped this source.</returns>
    public static Task<string?> WriteAsync(
        ConversionRequest request,
        string source,
        int index,
        DateTimeOffset now,
        Func<string, Task> write,
        int? width = null,
        int? height = null) =>
        PlaceAsync(request, source, index, now, write, folder: false, width, height);

    /// <summary>
    /// Same as <see cref="WriteAsync"/> for an output that is a folder of files, such as the pages of a PDF.
    /// <paramref name="write"/> fills an empty temporary folder, which is then renamed into place, so the
    /// folder never shows up half full and a failed write leaves nothing behind.
    /// </summary>
    public static Task<string?> WriteFolderAsync(ConversionRequest request, string source, int index, DateTimeOffset now, Func<string, Task> write) =>
        PlaceAsync(request, source, index, now, write, folder: true, width: null, height: null);

    private static async Task<string?> PlaceAsync(
        ConversionRequest request,
        string source,
        int index,
        DateTimeOffset now,
        Func<string, Task> write,
        bool folder,
        int? width,
        int? height)
    {
        var sequence = request.SourcePaths.Count > 1 ? index + 1 : (int?)null;
        string? Plan() => OutputPlanner.PathFor(request, source, now, Exists, sequence, width, height, folder);

        var target = Plan();
        if (target is null)
        {
            return null;
        }

        // A chosen output folder may not exist yet, or no longer.
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}{TempSuffix}");
        try
        {
            if (folder)
            {
                Directory.CreateDirectory(temp);
            }

            await write(temp).ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (folder)
                    {
                        Directory.Move(temp, target);
                    }
                    else
                    {
                        File.Move(temp, target, overwrite: false);
                    }

                    return target;
                }
                catch (IOException) when (Exists(target) && request.Conflict == ConflictPolicy.AutoNumber && attempt < 5)
                {
                    target = Plan() ?? throw new IOException($"{Path.GetFileName(target)} 已存在");
                }
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>A file and a folder cannot share a name, so either one takes it.</summary>
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Same as <see cref="WriteAsync"/> for a writer that is synchronous.</summary>
    public static string? Write(ConversionRequest request, string source, int index, DateTimeOffset now, Action<string> write, int? width = null, int? height = null) =>
        WriteAsync(request, source, index, now, temp =>
        {
            write(temp);
            return Task.CompletedTask;
        }, width, height).GetAwaiter().GetResult();

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
