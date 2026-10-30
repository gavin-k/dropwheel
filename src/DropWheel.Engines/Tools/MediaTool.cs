using System.ComponentModel;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Media;

namespace DropWheel.Engines.Tools;

/// <summary>
/// A Tools item for videos, run through the ffmpeg sidecar like <see cref="FfmpegConverter"/>. As with
/// <see cref="ImageTool"/>, each video gets a new file next to it named "{name}" plus <see cref="Suffix"/>, where
/// it goes and what happens on a name clash follow the output settings, a batch is all or nothing, and a video
/// the tool would leave as it was gets no file.
/// </summary>
public abstract class MediaTool(FfmpegConverter ffmpeg, TimeProvider? time) : IWheelOperation
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public abstract string Id { get; }

    public WheelMode Mode => WheelMode.Tools;

    public abstract string Label { get; }

    public abstract string Hint { get; }

    public abstract string? Glyph { get; }

    /// <summary>The formats it reads.</summary>
    protected virtual IReadOnlyCollection<string> Formats => FfmpegPlan.VideoInputs;

    /// <summary>Added to the source's name.</summary>
    protected abstract string Suffix { get; }

    /// <summary>Why a video was left as it was, such as "没有音轨".</summary>
    protected abstract string Unchanged { get; }

    /// <summary>When set, a new file that is not smaller than its source is thrown away and counts as unchanged.</summary>
    protected virtual bool OnlyIfSmaller => false;

    public bool AppliesTo(MediaKind kind) => kind == MediaKind.Video;

    public Availability Check(ConversionRequest request)
    {
        if (ffmpeg.Tools is null)
        {
            return Availability.Unavailable(FfmpegConverter.Missing) with { Cause = UnavailableCause.MissingOptionalModule };
        }

        foreach (var path in request.SourcePaths)
        {
            var format = FormatRegistry.FromPath(path);
            if (format is null || !Formats.Contains(format.Id))
            {
                return Availability.Unavailable(Unsupported(format) ?? L.T("不支持 {0}", format?.DisplayName ?? Path.GetFileName(path)));
            }

            if (!File.Exists(path))
            {
                return Availability.Unavailable(L.T("找不到 {0}", Path.GetFileName(path)));
            }
        }

        return Availability.Available;
    }

    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (ffmpeg.Tools is not { } tools)
        {
            return new(false, [], FfmpegConverter.Missing);
        }

        var now = _time.GetLocalNow();
        var unchanged = 0;
        long before = 0, after = 0;
        try
        {
            var encoders = await ffmpeg.EncodersAsync(tools).WaitAsync(cancellationToken).ConfigureAwait(false);
            var written = await OutputFiles.ForEachSourceAsync(request, progress, async (source, i, within) =>
            {
                var info = await MediaProbe.ProbeAsync(tools.Ffprobe, source, cancellationToken).ConfigureAwait(false);
                (string Format, FfmpegJob Job)? planned;
                try
                {
                    planned = Plan(info, FormatRegistry.FromPath(source)!.Id, encoders);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidDataException(L.T("{0} 无法处理：{1}", Path.GetFileName(source), ex.Message), ex);
                }

                if (planned is not var (format, job))
                {
                    unchanged++;
                    return null;
                }

                var size = new FileInfo(source).Length;
                long length = 0;
                var plan = request with { TargetFormatId = format, Naming = NamingTemplate.Default + Suffix };
                try
                {
                    var output = await OutputFiles.WriteAsync(plan, source, i, now, async temp =>
                    {
                        await FfmpegConverter.EncodeAsync(tools, source, job, info.Duration, temp, within, cancellationToken).ConfigureAwait(false);
                        length = new FileInfo(temp).Length;
                        if (OnlyIfSmaller && length >= size)
                        {
                            throw new NotSmallerException();
                        }
                    }, job.Width, job.Height, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (output is not null)
                    {
                        (before, after) = (before + size, after + length);
                    }

                    return output;
                }
                catch (NotSmallerException)
                {
                    unchanged++;
                    return null;
                }
            }, cancellationToken).ConfigureAwait(false);

            if (unchanged == request.SourcePaths.Count)
            {
                return new(true, []) { Skipped = true, Detail = Unchanged };
            }

            var notes = new[] { written.Count > 0 ? Detail(before, after) : null, unchanged > 0 ? L.T("另有 {0} 个{1}", unchanged, Unchanged) : null };
            return ConversionResult.Written(written) with { Detail = string.Join(L.T("；"), notes.OfType<string>()) is { Length: > 0 } d ? d : null };
        }
        catch (InvalidDataException ex)
        {
            return new(false, [], ex.Message);
        }
        catch (Win32Exception ex)
        {
            return new(false, [], L.T("无法启动 ffmpeg：{0}", ex.Message));
        }
    }

    /// <summary>The output format and the ffmpeg job for one video, or null when the tool would leave it as it is.</summary>
    /// <param name="formatId">The source's format.</param>
    /// <exception cref="InvalidDataException">The video cannot be done; the message says why, without its name.</exception>
    protected abstract (string Format, FfmpegJob Job)? Plan(MediaInfo info, string formatId, IReadOnlySet<string> encoders);

    /// <summary>Why a format is not covered, for a reason more useful than "不支持".</summary>
    protected virtual string? Unsupported(FileFormat? format) => null;

    /// <summary>One line for the task window about what was written.</summary>
    protected virtual string? Detail(long before, long after) => null;

    private sealed class NotSmallerException : Exception;
}
