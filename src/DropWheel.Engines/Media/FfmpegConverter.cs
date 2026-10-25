using System.ComponentModel;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;

namespace DropWheel.Engines.Media;

/// <summary>
/// Video and audio through the ffmpeg sidecar: each file is probed with ffprobe, then converted by one ffmpeg run
/// planned by <see cref="FfmpegPlan"/>. Progress comes from ffmpeg's -progress output against the probed duration.
/// </summary>
public sealed class FfmpegConverter : IConverter
{
    private const string Missing = "找不到 ffmpeg 组件，请重新安装 DropWheel";

    private readonly Lazy<FfmpegTools?> _tools;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Task<IReadOnlySet<string>>? _encoders;

    /// <param name="directory">Where ffmpeg and ffprobe are; null searches, see <see cref="FfmpegLocator"/>.</param>
    public FfmpegConverter(string? directory = null, TimeProvider? time = null)
    {
        _tools = new(() => FfmpegLocator.Find(directory));
        _time = time ?? TimeProvider.System;
    }

    public string Id => "ffmpeg";

    /// <summary>Null when neither the bundled sidecar nor one on the PATH was found.</summary>
    public FfmpegTools? Tools => _tools.Value;

    public IEnumerable<ConversionEdge> Edges =>
        (from i in FfmpegPlan.VideoInputs
         from o in FfmpegPlan.VideoOutputs.Concat(FfmpegPlan.AudioOutputs).Append("gif")
         where i != o
         select new ConversionEdge(i, o))
        .Concat(
         from i in FfmpegPlan.AudioInputs
         from o in FfmpegPlan.AudioOutputs
         where i != o
         select new ConversionEdge(i, o));

    public Availability Check(ConversionRequest request) => _tools.Value is null
        ? Availability.Unavailable(Missing) with { Cause = UnavailableCause.MissingOptionalModule }
        : Availability.Available;

    public QualityEstimate? Estimate(ConversionRequest request)
    {
        var from = FormatRegistry.FromPath(request.SourcePaths[0]);
        var to = request.TargetFormatId;
        if (from is null)
        {
            return null;
        }

        if (to == "gif")
        {
            return new(QualityLevel.Medium, ["GIF 只有 256 色，每秒 10 帧，宽度不超过 480，没有声音"]);
        }

        var level = QualityLevel.High;
        var reasons = new List<string>();
        if (FfmpegPlan.Lossless.Contains(to) && !FfmpegPlan.Lossless.Contains(from.Id))
        {
            reasons.Add("无损格式不会补回已丢失的细节，文件会变大");
        }

        if (from.Id == "mkv" && FfmpegPlan.VideoOutputs.Contains(to))
        {
            reasons.Add("内嵌字幕轨不保留");
        }

        if (to == "avi")
        {
            level = QualityLevel.Medium;
            reasons.Add("AVI 只能用较旧的 MPEG-4 编码，同样画质文件更大");
        }

        if (to == "webm" && from.Id != "webm")
        {
            reasons.Add("WebM 要重新编码为 AV1，耗时较长");
        }

        return reasons.Count > 0 ? new(level, reasons) : null;
    }

    /// <summary>
    /// Starts asking ffmpeg for its encoders in the background, so the first conversion does not wait for it.
    /// </summary>
    public void Warmup()
    {
        if (_tools.Value is { } tools)
        {
            _ = EncodersAsync(tools).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    /// <summary>All or nothing, see <see cref="OutputFiles.ForEachSourceAsync"/>.</summary>
    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var target = request.TargetFormatId;
        if (target != "gif" && !FfmpegPlan.VideoOutputs.Contains(target) && !FfmpegPlan.AudioOutputs.Contains(target))
        {
            return new(false, [], $"不支持输出 {target}");
        }

        if (_tools.Value is not { } tools)
        {
            return new(false, [], Missing);
        }

        try
        {
            var encoders = await EncodersAsync(tools).WaitAsync(cancellationToken).ConfigureAwait(false);
            var written = await OutputFiles.ForEachSourceAsync(request, progress,
                (source, i, within) => Convert(tools, encoders, request, source, i, within, cancellationToken), cancellationToken).ConfigureAwait(false);
            return new(true, written);
        }
        catch (InvalidDataException ex)
        {
            return new(false, [], ex.Message);
        }
        catch (Win32Exception ex)
        {
            return new(false, [], $"无法启动 ffmpeg：{ex.Message}");
        }
    }

    private async Task<string?> Convert(
        FfmpegTools tools,
        IReadOnlySet<string> encoders,
        ConversionRequest request,
        string source,
        int index,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var info = await MediaProbe.ProbeAsync(tools.Ffprobe, source, cancellationToken).ConfigureAwait(false);
        var job = FfmpegPlan.Build(info, request.TargetFormatId, encoders);
        return await OutputFiles.WriteAsync(request, source, index, _time.GetLocalNow(), async temp =>
        {
            var result = await new SidecarProcess(tools.Ffmpeg).RunAsync(
                FfmpegPlan.Command(source, job, temp),
                line =>
                {
                    if (FfmpegPlan.Progress(line, info.Duration) is { } p)
                    {
                        progress.Report(p);
                    }
                },
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidDataException(Failure(source, result));
            }
        }, job.Width, job.Height).ConfigureAwait(false);
    }

    private static string Failure(string source, SidecarResult result)
    {
        if (result.StderrTail.Any(l => l.Contains("could not find any MFT", StringComparison.OrdinalIgnoreCase)))
        {
            return "系统缺少 H.264 编码器（Windows N 版需安装媒体功能包）";
        }

        var detail = MediaProbe.Detail(result.StderrTail);
        return $"{Path.GetFileName(source)} 转换失败{(detail.Length > 0 ? detail : $"（ffmpeg 退出码 {result.ExitCode}）")}";
    }

    /// <summary>Asked once and cached; a failed attempt is retried by the next caller.</summary>
    private Task<IReadOnlySet<string>> EncodersAsync(FfmpegTools tools)
    {
        lock (_gate)
        {
            if (_encoders is null || _encoders.IsFaulted || _encoders.IsCanceled)
            {
                _encoders = QueryEncodersAsync(tools.Ffmpeg);
            }

            return _encoders;
        }
    }

    private static async Task<IReadOnlySet<string>> QueryEncodersAsync(string ffmpeg)
    {
        var lines = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await new SidecarProcess(ffmpeg).RunAsync(["-hide_banner", "-encoders"], lines.Add, timeout.Token).ConfigureAwait(false);
        var encoders = FfmpegPlan.ParseEncoders(lines);
        if (result.ExitCode != 0 || encoders.Count == 0)
        {
            var detail = MediaProbe.Detail(result.StderrTail);
            throw new InvalidDataException($"ffmpeg 无法运行{(detail.Length > 0 ? detail : $"（退出码 {result.ExitCode}）")}");
        }

        return encoders;
    }
}
