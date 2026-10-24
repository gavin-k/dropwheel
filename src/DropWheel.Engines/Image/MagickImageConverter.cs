using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using ImageMagick;

namespace DropWheel.Engines.Image;

/// <summary>
/// Image ↔ image via Magick.NET. M0 scope: the raster formats below, first frame only. HEIC encode
/// availability depends on the system HEVC extension and is reported, not hidden.
/// </summary>
public sealed class MagickImageConverter(TimeProvider? time = null) : IConverter
{
    private static readonly Dictionary<string, MagickFormat> Formats = new(StringComparer.Ordinal)
    {
        ["jpg"] = MagickFormat.Jpeg,
        ["png"] = MagickFormat.Png,
        ["webp"] = MagickFormat.WebP,
        ["avif"] = MagickFormat.Avif,
        ["gif"] = MagickFormat.Gif,
        ["bmp"] = MagickFormat.Bmp,
        ["tiff"] = MagickFormat.Tiff,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "magick";

    public IEnumerable<ConversionEdge> Edges =>
        from i in Formats.Keys
        from o in Formats.Keys.Append("heic")
        where i != o
        select new ConversionEdge(i, o);

    public Availability Check(ConversionRequest request)
    {
        if (request.TargetFormatId == "heic" && !SystemCodecs.HasHevcEncoder())
        {
            return Availability.Unavailable("HEIC 编码需要系统已安装「HEVC 视频扩展」") with { Cause = UnavailableCause.MissingSystemCodec };
        }

        return Availability.Available;
    }

    public QualityEstimate? Estimate(ConversionRequest request)
    {
        var from = FormatRegistry.FromPath(request.SourcePaths[0])?.Id;
        return (from, request.TargetFormatId) switch
        {
            ("jpg", "png") => new(QualityLevel.High, ["无损，文件会变大"]),
            (_, "gif") => new(QualityLevel.Medium, ["GIF 仅 256 色"]),
            _ => null,
        };
    }

    /// <summary>
    /// All or nothing: if one source fails or the job is canceled, outputs already written by this run
    /// are deleted (they did not exist before it) and nothing is reported.
    /// </summary>
    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Formats.TryGetValue(request.TargetFormatId, out var format))
        {
            return new(false, [], $"不支持输出 {request.TargetFormatId}");
        }

        var written = new List<string>();
        try
        {
            for (var i = 0; i < request.SourcePaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = request.SourcePaths[i];
                var output = await Task.Run(() => Convert(request, source, i, format), cancellationToken).ConfigureAwait(false);
                if (output is not null)
                {
                    written.Add(output);
                }

                progress.Report((i + 1.0) / request.SourcePaths.Count);
            }

            return new(true, written);
        }
        catch (Exception ex)
        {
            foreach (var path in written)
            {
                TryDelete(path);
            }

            if (ex is MagickException)
            {
                return new(false, [], $"{Path.GetFileName(ex.Data["source"] as string)} 无法转换：{ex.Message}");
            }

            throw;
        }
    }

    /// <returns>The written path, or <c>null</c> when the conflict policy skipped this source.</returns>
    private string? Convert(ConversionRequest request, string source, int index, MagickFormat format)
    {
        var sequence = request.SourcePaths.Count > 1 ? index + 1 : (int?)null;
        try
        {
            using var image = new MagickImage(source);
            image.AutoOrient();
            var (width, height) = ((int)image.Width, (int)image.Height);
            string? Plan() => OutputPlanner.PathFor(request, source, _time.GetLocalNow(), File.Exists, sequence, width, height);

            var target = Plan();
            if (target is null)
            {
                return null;
            }

            var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.dropwheel-tmp");
            try
            {
                image.Write(temp, format);
                // Never overwrite: a file that appeared after planning gets a new number (AutoNumber),
                // any other policy fails rather than clobber it.
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Move(temp, target, overwrite: false);
                        return target;
                    }
                    catch (IOException) when (File.Exists(target) && request.Conflict == ConflictPolicy.AutoNumber && attempt < 5)
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
        catch (MagickException ex)
        {
            ex.Data["source"] = source;
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class SystemCodecs
{
    /// <summary>TODO: probe WIC for the HEVC encoder (Microsoft.HEVCVideoExtension) instead of assuming.</summary>
    public static bool HasHevcEncoder() => false;
}
