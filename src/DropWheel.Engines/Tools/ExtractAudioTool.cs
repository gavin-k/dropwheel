using DropWheel.Engines.Media;

namespace DropWheel.Engines.Tools;

/// <summary>
/// The first audio track of a video as an audio file, taken out as it is where an audio format holds it: AAC
/// and ALAC as M4A, MP3, Opus, Vorbis as OGG, FLAC, PCM as WAV. Anything else becomes AAC in M4A.
/// </summary>
public sealed class ExtractAudioTool(FfmpegConverter ffmpeg, TimeProvider? time = null) : MediaTool(ffmpeg, time)
{
    public override string Id => "tool.extract-audio";

    public override string Label => L.T("提取音频");

    public override string Hint => L.T("尽量原样");

    protected override string Suffix => "";

    protected override string Unchanged => L.T("没有音轨");

    protected override (string Format, FfmpegJob Job)? Plan(MediaInfo info, string formatId, IReadOnlySet<string> encoders)
    {
        if (info.Audio is not [var audio, ..])
        {
            return null;
        }

        var target = audio.Codec switch
        {
            "mp3" => "mp3",
            "opus" => "opus",
            "vorbis" => "ogg",
            "flac" => "flac",
            ['p', 'c', 'm', '_', ..] => "wav",
            _ => "m4a",
        };
        return (target, FfmpegPlan.Build(info, target, encoders));
    }
}
