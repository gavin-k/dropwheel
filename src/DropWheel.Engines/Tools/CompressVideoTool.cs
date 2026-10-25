using DropWheel.Engines.Media;

namespace DropWheel.Engines.Tools;

/// <summary>
/// Makes a video smaller to send: an MP4 at most 1080p, see <see cref="FfmpegPlan.Compress"/>. The new file is
/// kept only when it is smaller.
/// </summary>
public sealed class CompressVideoTool(FfmpegConverter ffmpeg, TimeProvider? time = null) : MediaTool(ffmpeg, time)
{
    public override string Id => "tool.compress-video";

    public override string Label => "压缩视频";

    public override string Hint => "变小才保留";

    protected override string Suffix => "-压缩";

    protected override string Unchanged => "已经很小，压缩后没有变小";

    protected override bool OnlyIfSmaller => true;

    protected override string? Detail(long before, long after) => $"{CompressImageTool.Size(before)} → {CompressImageTool.Size(after)}";

    protected override (string Format, FfmpegJob Job)? Plan(MediaInfo info, string formatId, IReadOnlySet<string> encoders) =>
        ("mp4", FfmpegPlan.Compress(info, encoders));
}
