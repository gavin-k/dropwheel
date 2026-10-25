using System.Globalization;

namespace DropWheel.Engines.Media;

/// <param name="Arguments">Everything between the input and the output file: maps, codecs, filters and -f.</param>
/// <param name="Width">Of the output picture, for the {w} placeholder; null for audio.</param>
public sealed record FfmpegJob(IReadOnlyList<string> Arguments, int? Width, int? Height);

/// <summary>
/// Decides how ffmpeg converts one probed file. Streams the target container takes as they are are copied,
/// everything else is re-encoded with the first encoder this ffmpeg build has. Only the first video stream
/// (cover art skipped) and the audio streams are kept; subtitles, data and attachments are dropped.
/// The bundled LGPL build has no libx264: H.264 goes through Windows Media Foundation (h264_mf), WebM uses
/// AV1 (SVT-AV1) because the ARM64 build has no libvpx.
/// </summary>
public static class FfmpegPlan
{
    public static readonly string[] VideoOutputs = ["mp4", "mov", "mkv", "webm", "avi"];
    public static readonly string[] VideoInputs = [.. VideoOutputs, "rmvb"];
    public static readonly string[] AudioOutputs = ["mp3", "aac", "m4a", "wav", "flac", "ogg", "opus", "aiff"];
    public static readonly string[] AudioInputs = [.. AudioOutputs, "amr"];

    /// <summary>Lossless targets, which make files bigger without making lossy sources better.</summary>
    public static readonly string[] Lossless = ["wav", "flac", "aiff"];

    /// <summary>For <see cref="Compress"/>: 1080p, or 1080 wide for a portrait video.</summary>
    private const int MaxShortSide = 1080;

    /// <summary>The output is a temp file without the real extension, so the muxer is always named.</summary>
    private static readonly Dictionary<string, string> Muxers = new()
    {
        ["mp4"] = "mp4", ["mov"] = "mov", ["mkv"] = "matroska", ["webm"] = "webm", ["avi"] = "avi", ["gif"] = "gif",
        ["mp3"] = "mp3", ["aac"] = "adts", ["m4a"] = "ipod", ["wav"] = "wav", ["flac"] = "flac", ["ogg"] = "ogg",
        ["opus"] = "opus", ["aiff"] = "aiff",
    };

    private static readonly Dictionary<string, string[]> CopyVideo = new()
    {
        ["mp4"] = ["h264", "hevc", "mpeg4", "av1"],
        ["mov"] = ["h264", "hevc", "mpeg4", "prores", "mjpeg"],
        ["mkv"] = ["h264", "hevc", "av1", "vp8", "vp9", "mpeg4", "mpeg2video", "mpeg1video", "prores", "mjpeg"],
        ["webm"] = ["vp8", "vp9", "av1"],
        ["avi"] = ["mpeg4", "mjpeg"],
    };

    private static readonly Dictionary<string, string[]> CopyAudio = new()
    {
        ["mp4"] = ["aac", "mp3", "ac3", "eac3", "alac"],
        ["mov"] = ["aac", "mp3", "ac3", "eac3", "alac", "pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_s24be"],
        ["mkv"] = ["aac", "mp3", "mp2", "ac3", "eac3", "dts", "truehd", "opus", "vorbis", "flac", "alac", "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le"],
        ["webm"] = ["opus", "vorbis"],
        ["avi"] = ["mp3", "ac3", "pcm_s16le"],
        ["mp3"] = ["mp3"],
        ["aac"] = ["aac"],
        ["m4a"] = ["aac", "alac"],
        ["wav"] = ["pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le"],
        ["flac"] = ["flac"],
        ["ogg"] = ["vorbis"],
        ["opus"] = ["opus"],
        ["aiff"] = ["pcm_s16be", "pcm_s24be"],
    };

    private const string GifFilter =
        "fps=10,scale='min(480,iw)':-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5";

    /// <exception cref="InvalidDataException">The file lacks the stream the target needs, or this build has no encoder for it.</exception>
    public static FfmpegJob Build(MediaInfo info, string target, IReadOnlySet<string> encoders)
    {
        if (target == "gif")
        {
            return Gif(info);
        }

        if (VideoOutputs.Contains(target))
        {
            return Video(info, target, encoders);
        }

        if (AudioOutputs.Contains(target))
        {
            return Audio(info, target, encoders);
        }

        throw new InvalidDataException($"不支持输出 {target}");
    }

    /// <summary>The whole command line. Paths get the file: protocol so a name like "a:b" is never taken for a protocol.</summary>
    public static IReadOnlyList<string> Command(string source, FfmpegJob job, string output) =>
    [
        "-hide_banner", "-nostdin", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
        "-i", "file:" + source,
        .. job.Arguments,
        "-y", "file:" + output,
    ];

    /// <summary>The fraction done, from an "out_time_us=…" line of -progress output.</summary>
    public static double? Progress(string line, TimeSpan? duration)
    {
        if (duration is not { } d || d <= TimeSpan.Zero)
        {
            return null;
        }

        var eq = line.IndexOf('=');
        // out_time_ms carries microseconds as well, a long-standing ffmpeg quirk.
        if (eq < 0 || line[..eq] is not ("out_time_us" or "out_time_ms")
            || !long.TryParse(line.AsSpan(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
        {
            return null;
        }

        return Math.Clamp(us / d.TotalMicroseconds, 0, 1);
    }

    /// <summary>The names from the listing of <c>ffmpeg -encoders</c>, which starts after a "------" line.</summary>
    public static IReadOnlySet<string> ParseEncoders(IEnumerable<string> lines)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var listing = false;
        foreach (var line in lines)
        {
            if (!listing)
            {
                listing = line.TrimStart().StartsWith("---", StringComparison.Ordinal);
                continue;
            }

            if (line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries) is [_, var name, ..])
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// For h264_mf, which has no constant-quality mode: 0.1 bit per pixel, within 0.5 to 20 Mbit/s, and at most
    /// twice the source's rate so a small file does not balloon.
    /// </summary>
    public static long H264BitRate(MediaTrack video, long? containerBitRate)
    {
        var w = video.Width ?? 1280;
        var h = video.Height ?? 720;
        var fps = video.FrameRate is double f and > 0 and < 240 ? f : 30;
        var rate = Math.Clamp(0.1 * w * h * fps, 500_000, 20_000_000);
        if ((video.BitRate ?? containerBitRate) is long source and > 0)
        {
            rate = Math.Min(rate, Math.Max(2.0 * source, 500_000));
        }

        return (long)rate;
    }

    private static FfmpegJob Video(MediaInfo info, string target, IReadOnlySet<string> encoders)
    {
        var video = info.Video;
        var audio = info.Audio;
        if (video is null && audio.Count == 0)
        {
            throw new InvalidDataException("没有视频或音频轨");
        }

        var args = new List<string>();
        int? width = video?.Width, height = video?.Height;
        if (video is not null)
        {
            args.AddRange(["-map", $"0:{video.Index}"]);
        }

        foreach (var a in audio)
        {
            args.AddRange(["-map", $"0:{a.Index}"]);
        }

        if (video is not null)
        {
            if (CopyVideo[target].Contains(video.Codec))
            {
                args.AddRange(CopyVideoInto(target, video.Codec));
            }
            else
            {
                args.AddRange(VideoEncoder(target, video, info.BitRate, encoders));
                if (video.Width % 2 == 1 || video.Height % 2 == 1)
                {
                    // 4:2:0 needs even dimensions.
                    args.AddRange(["-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2"]);
                    width -= width % 2;
                    height -= height % 2;
                }
            }
        }

        if (audio.Count > 0)
        {
            args.AddRange(audio.All(a => CopyAudio[target].Contains(a.Codec))
                ? ["-c:a", "copy"]
                : AudioEncoder(target, audio.Max(a => a.Channels ?? 2), audio.Max(a => a.BitsPerSample ?? 0), encoders));
        }

        return new(Finish(args, target), width, height);
    }

    /// <summary>
    /// The picture as it is, in the container it came in, without sound. Only the first video stream is kept:
    /// subtitles and cover art go too.
    /// </summary>
    /// <exception cref="InvalidDataException">There is no picture.</exception>
    public static FfmpegJob Mute(MediaInfo info, string container)
    {
        var video = info.Video ?? throw new InvalidDataException("没有视频画面");
        List<string> args = ["-map", $"0:{video.Index}", .. CopyVideoInto(container, video.Codec)];
        return new(Finish(args, container), video.Width, video.Height);
    }

    /// <summary>
    /// A smaller MP4 to send: at most 1080 pixels on the short side, H.264 at about 0.05 bit per pixel and never
    /// above 60% of the source's rate, AAC at 128 kbit/s unless the source's AAC is already that small. Whether
    /// the result is actually smaller is only known afterwards.
    /// </summary>
    /// <exception cref="InvalidDataException">There is no picture, or this build has no encoder for it.</exception>
    public static FfmpegJob Compress(MediaInfo info, IReadOnlySet<string> encoders)
    {
        var video = info.Video ?? throw new InvalidDataException("没有视频画面");
        var args = new List<string> { "-map", $"0:{video.Index}" };
        foreach (var a in info.Audio)
        {
            args.AddRange(["-map", $"0:{a.Index}"]);
        }

        int? width = null, height = null;
        if (video is { Width: int w and > 0, Height: int h and > 0 })
        {
            // Display size: ffmpeg turns a rotated picture upright before the filter runs.
            var scale = Math.Min(1.0, (double)MaxShortSide / Math.Min(w, h));
            (width, height) = (Even(w * scale), Even(h * scale));
            args.AddRange(["-vf", FormattableString.Invariant($"scale={width}:{height}")]);
        }
        else
        {
            args.AddRange(["-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2"]);
        }

        var fps = video.FrameRate is double f and > 0 and < 240 ? f : 30;
        var rate = Math.Clamp(0.05 * (width ?? 1280) * (height ?? 720) * fps, 300_000, 6_000_000);
        if ((video.BitRate ?? info.BitRate) is long source and > 0)
        {
            rate = Math.Max(Math.Min(rate, 0.6 * source), 100_000);
        }

        args.AddRange(First(encoders, "H.264",
            ["-c:v", "h264_mf", "-b:v", ((long)rate).ToString(CultureInfo.InvariantCulture)],
            ["-c:v", "libx264", "-crf", "26", "-pix_fmt", "yuv420p"],
            ["-c:v", "mpeg4", "-q:v", "5"]));
        if (info.Audio.Count > 0)
        {
            args.AddRange(info.Audio.All(a => a is { Codec: "aac", BitRate: <= 160_000 })
                ? ["-c:a", "copy"]
                : First(encoders, "AAC", ["-c:a", "aac", "-b:a", info.Audio.Max(a => a.Channels ?? 2) > 2 ? "256k" : "128k"]));
        }

        return new(Finish(args, "mp4"), width, height);

        static int Even(double size) => Math.Max(2, (int)Math.Round(size / 2) * 2);
    }

    /// <summary>Stream-copies a video stream of <paramref name="codec"/> into <paramref name="target"/>.</summary>
    private static string[] CopyVideoInto(string target, string codec) => (codec, target) switch
    {
        // The tag QuickTime and the Windows Photos app expect; ffmpeg defaults to hev1.
        ("hevc", "mp4" or "mov") => ["-c:v", "copy", "-tag:v", "hvc1"],
        // DivX-style packed B-frames are only valid in AVI.
        ("mpeg4", not "avi") => ["-c:v", "copy", "-bsf:v", "mpeg4_unpack_bframes"],
        _ => ["-c:v", "copy"],
    };

    private static FfmpegJob Audio(MediaInfo info, string target, IReadOnlySet<string> encoders)
    {
        var a = info.Audio is [var first, ..] ? first : throw new InvalidDataException("没有音轨");
        List<string> args = ["-map", $"0:{a.Index}"];
        args.AddRange(CopyAudio[target].Contains(a.Codec)
            ? ["-c:a", "copy"]
            : AudioEncoder(target, a.Channels ?? 2, a.BitsPerSample ?? 0, encoders));
        return new(Finish(args, target), null, null);
    }

    private static FfmpegJob Gif(MediaInfo info)
    {
        var video = info.Video ?? throw new InvalidDataException("没有视频画面");
        int? width = null, height = null;
        if (video is { Width: int w and > 0, Height: int h })
        {
            width = Math.Min(480, w);
            height = (int)Math.Round(h * (double)width / w);
        }

        return new(["-map", $"0:{video.Index}", "-vf", GifFilter, "-loop", "0", "-f", Muxers["gif"]], width, height);
    }

    private static List<string> Finish(List<string> args, string target)
    {
        if (target is "mp4" or "mov" or "m4a")
        {
            // Index at the front, so the file plays while it is still downloading or being copied.
            args.AddRange(["-movflags", "+faststart"]);
        }

        args.AddRange(["-f", Muxers[target]]);
        return args;
    }

    private static string[] VideoEncoder(string target, MediaTrack video, long? containerBitRate, IReadOnlySet<string> encoders) => target switch
    {
        "webm" => First(encoders, "WebM 视频",
            ["-c:v", "libsvtav1", "-preset", "10", "-crf", "35", "-pix_fmt", "yuv420p"],
            ["-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "33", "-row-mt", "1", "-cpu-used", "4", "-pix_fmt", "yuv420p"]),
        "avi" => First(encoders, "MPEG-4",
            ["-c:v", "mpeg4", "-q:v", "3", "-tag:v", "xvid"]),
        _ => First(encoders, "H.264",
            ["-c:v", "h264_mf", "-b:v", H264BitRate(video, containerBitRate).ToString(CultureInfo.InvariantCulture)],
            ["-c:v", "libx264", "-crf", "20", "-preset", "veryfast", "-pix_fmt", "yuv420p"],
            ["-c:v", "mpeg4", "-q:v", "3"]),
    };

    /// <param name="bits">Bits per sample of the source, 0 when unknown or lossy; above 16 keeps 24-bit PCM.</param>
    private static string[] AudioEncoder(string target, int channels, int bits, IReadOnlySet<string> encoders)
    {
        // MP3 has at most two channels; libopus refuses some surround layouts.
        string[] stereo = channels > 2 ? ["-ac", "2"] : [];
        var deep = bits > 16;
        return target switch
        {
            "mp4" or "mov" or "mkv" or "aac" or "m4a" => First(encoders, "AAC",
                ["-c:a", "aac", "-b:a", channels > 2 ? "384k" : "192k"]),
            "webm" => First(encoders, "Opus",
                ["-c:a", "libopus", "-b:a", "128k", .. stereo],
                ["-c:a", "libvorbis", "-q:a", "5", .. stereo]),
            "opus" => First(encoders, "Opus",
                ["-c:a", "libopus", "-b:a", "128k", .. stereo],
                ["-c:a", "opus", "-strict", "experimental", "-b:a", "128k", "-ac", "2"]),
            "avi" => First(encoders, "MP3",
                ["-c:a", "libmp3lame", "-b:a", "192k", .. stereo],
                ["-c:a", "mp3_mf", "-b:a", "192k", .. stereo]),
            "mp3" => First(encoders, "MP3",
                ["-c:a", "libmp3lame", "-q:a", "2", .. stereo],
                ["-c:a", "mp3_mf", "-b:a", "192k", .. stereo]),
            "ogg" => First(encoders, "Vorbis",
                ["-c:a", "libvorbis", "-q:a", "5"]),
            "wav" => ["-c:a", deep ? "pcm_s24le" : "pcm_s16le"],
            "aiff" => ["-c:a", deep ? "pcm_s24be" : "pcm_s16be"],
            "flac" => ["-c:a", "flac", "-sample_fmt", deep ? "s32" : "s16"],
            _ => throw new InvalidDataException($"不支持输出 {target}"),
        };
    }

    /// <summary>The first choice whose encoder (the value after -c:x) this build has.</summary>
    private static string[] First(IReadOnlySet<string> encoders, string what, params string[][] choices) =>
        choices.FirstOrDefault(c => encoders.Contains(c[1]))
        ?? throw new InvalidDataException($"这个 ffmpeg 没有 {what} 编码器");
}
