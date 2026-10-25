using DropWheel.Core.Conversion;
using DropWheel.Engines.Media;

namespace DropWheel.Engines.Tools;

/// <summary>The same video without its sound, in the same format; the picture is copied, not encoded again. See <see cref="FfmpegPlan.Mute"/>.</summary>
public sealed class MuteVideoTool(FfmpegConverter ffmpeg, TimeProvider? time = null) : MediaTool(ffmpeg, time)
{
    public override string Id => "tool.mute";

    public override string Label => "去掉声音";

    public override string Hint => "画面不动";

    protected override IReadOnlyCollection<string> Formats => FfmpegPlan.VideoOutputs;

    protected override string Suffix => "-无声";

    protected override string Unchanged => "本来就没有声音";

    protected override string? Unsupported(FileFormat? format) => format?.Id == "rmvb" ? "RMVB 不能原样去掉声音，先转成 MP4" : null;

    protected override (string Format, FfmpegJob Job)? Plan(MediaInfo info, string formatId, IReadOnlySet<string> encoders)
    {
        var job = FfmpegPlan.Mute(info, formatId);
        return info.Audio.Count == 0 ? null : (formatId, job);
    }
}
