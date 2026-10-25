using DropWheel.Engines.Media;
using FluentAssertions;

namespace DropWheel.Engines.Tests;

public sealed class FfmpegPlanTests
{
    /// <summary>What the bundled LGPL builds have in common (no libx264, no libvpx on ARM64).</summary>
    private static readonly HashSet<string> Bundled =
        ["h264_mf", "mpeg4", "libsvtav1", "gif", "aac", "libmp3lame", "libopus", "libvorbis", "flac", "pcm_s16le", "pcm_s24le", "pcm_s16be", "pcm_s24be"];

    private static MediaInfo Info(params MediaTrack[] streams) => new(TimeSpan.FromSeconds(10), 4_000_000, streams);

    private static MediaTrack H264(int index = 0, int w = 1920, int h = 1080) => new(index, "video", "h264", w, h, 30, 4_000_000);

    private static MediaTrack Aac(int index = 1, int channels = 2) => new(index, "audio", "aac", Channels: channels);

    private static string Joined(FfmpegJob job) => string.Join(' ', job.Arguments);

    [Fact]
    public void Codecs_the_target_container_takes_are_copied()
    {
        var job = FfmpegPlan.Build(Info(H264(), Aac()), "mkv", Bundled);

        Joined(job).Should().Be("-map 0:0 -map 0:1 -c:v copy -c:a copy -f matroska");
        (job.Width, job.Height).Should().Be((1920, 1080));
    }

    [Fact]
    public void Hevc_copied_into_mp4_gets_the_hvc1_tag_and_faststart()
    {
        var job = FfmpegPlan.Build(Info(new(0, "video", "hevc", 3840, 2160, 30), Aac()), "mp4", Bundled);

        Joined(job).Should().Be("-map 0:0 -map 0:1 -c:v copy -tag:v hvc1 -c:a copy -movflags +faststart -f mp4");
    }

    [Fact]
    public void Video_the_container_cannot_take_is_encoded_with_media_foundation_h264()
    {
        var job = FfmpegPlan.Build(Info(new(0, "video", "rv40", 1280, 720, 25), new(1, "audio", "cook", Channels: 2)), "mp4", Bundled);

        Joined(job).Should().Be("-map 0:0 -map 0:1 -c:v h264_mf -b:v 2304000 -c:a aac -b:a 192k -movflags +faststart -f mp4");
    }

    [Fact]
    public void Libx264_is_used_where_media_foundation_is_missing()
    {
        var job = FfmpegPlan.Build(Info(new(0, "video", "vp9", 1280, 720, 25), Aac()), "mp4", new HashSet<string> { "libx264", "aac" });

        Joined(job).Should().Contain("-c:v libx264 -crf 20 -preset veryfast -pix_fmt yuv420p");
    }

    [Fact]
    public void Odd_dimensions_are_scaled_down_to_even_ones_and_reported()
    {
        var job = FfmpegPlan.Build(Info(new MediaTrack(0, "video", "ffv1", 321, 241, 10)), "mp4", Bundled);

        Joined(job).Should().Contain("-vf scale=trunc(iw/2)*2:trunc(ih/2)*2");
        (job.Width, job.Height).Should().Be((320, 240));
    }

    [Theory]
    [InlineData(1920, 1080, 30.0, null, 6_220_800)]
    [InlineData(3840, 2160, 60.0, null, 20_000_000)]
    [InlineData(320, 240, 25.0, null, 500_000)]
    [InlineData(1920, 1080, 30.0, 1_000_000L, 2_000_000)]
    [InlineData(1920, 1080, 30.0, 100_000L, 500_000)]
    public void H264_bit_rate_follows_the_picture_and_never_balloons_a_small_source(int w, int h, double fps, long? source, long expected) =>
        FfmpegPlan.H264BitRate(new(0, "video", "rv40", w, h, fps, source), null).Should().Be(expected);

    [Fact]
    public void Webm_uses_svt_av1_and_falls_back_to_vp9()
    {
        Joined(FfmpegPlan.Build(Info(H264(), Aac()), "webm", Bundled))
            .Should().Be("-map 0:0 -map 0:1 -c:v libsvtav1 -preset 10 -crf 35 -pix_fmt yuv420p -c:a libopus -b:a 128k -f webm");
        Joined(FfmpegPlan.Build(Info(H264(), Aac()), "webm", new HashSet<string> { "libvpx-vp9", "libopus" })).Should().Contain("-c:v libvpx-vp9");
    }

    [Fact]
    public void A_missing_encoder_is_named_in_the_error()
    {
        var act = () => FfmpegPlan.Build(Info(H264(), Aac()), "webm", new HashSet<string> { "aac" });

        act.Should().Throw<InvalidDataException>().WithMessage("*WebM*");
    }

    [Fact]
    public void Cover_art_is_not_taken_for_the_video()
    {
        var cover = new MediaTrack(0, "video", "mjpeg", 600, 600, AttachedPicture: true);

        var job = FfmpegPlan.Build(Info(cover, new(1, "audio", "mp3", Channels: 2)), "mp4", Bundled);

        Joined(job).Should().Be("-map 0:1 -c:a copy -movflags +faststart -f mp4");
        job.Width.Should().BeNull();
    }

    [Fact]
    public void Audio_target_takes_the_first_audio_stream_and_copies_a_matching_codec()
    {
        var job = FfmpegPlan.Build(Info(H264(), Aac(1), Aac(2)), "m4a", Bundled);

        Joined(job).Should().Be("-map 0:1 -c:a copy -movflags +faststart -f ipod");
        job.Width.Should().BeNull();
    }

    [Fact]
    public void Audio_target_without_audio_fails()
    {
        var act = () => FfmpegPlan.Build(Info(H264()), "mp3", Bundled);

        act.Should().Throw<InvalidDataException>().WithMessage("没有音轨");
    }

    [Fact]
    public void Surround_is_downmixed_for_mp3()
    {
        Joined(FfmpegPlan.Build(Info(Aac(0, channels: 6)), "mp3", Bundled)).Should().Be("-map 0:0 -c:a libmp3lame -q:a 2 -ac 2 -f mp3");
    }

    [Theory]
    [InlineData(24, "flac", "-c:a flac -sample_fmt s32")]
    [InlineData(16, "flac", "-c:a flac -sample_fmt s16")]
    [InlineData(0, "wav", "-c:a pcm_s16le")]
    [InlineData(24, "aiff", "-c:a pcm_s24be")]
    public void Lossless_targets_keep_the_source_bit_depth(int bits, string target, string expected)
    {
        var source = new MediaTrack(0, "audio", bits == 0 ? "mp3" : "pcm_s24le", BitsPerSample: bits == 0 ? null : bits, Channels: 2);

        Joined(FfmpegPlan.Build(Info(source), target, Bundled)).Should().Contain(expected);
    }

    [Fact]
    public void Gif_keeps_only_the_picture_at_most_480_wide()
    {
        var job = FfmpegPlan.Build(Info(H264(), Aac()), "gif", Bundled);

        job.Arguments.Should().StartWith(["-map", "0:0", "-vf"]).And.EndWith(["-loop", "0", "-f", "gif"]);
        (job.Width, job.Height).Should().Be((480, 270));
    }

    [Fact]
    public void Command_wraps_both_paths_in_the_file_protocol()
    {
        var job = new FfmpegJob(["-c:a", "copy", "-f", "mp3"], null, null);

        FfmpegPlan.Command(@"C:\a:b.mp4", job, @"C:\out.tmp").Should().Equal(
            "-hide_banner", "-nostdin", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
            "-i", @"file:C:\a:b.mp4", "-c:a", "copy", "-f", "mp3", "-y", @"file:C:\out.tmp");
    }

    [Theory]
    [InlineData("out_time_us=2500000", 0.25)]
    [InlineData("out_time_ms=5000000", 0.5)]
    [InlineData("out_time_us=99000000", 1.0)]
    [InlineData("out_time_us=N/A", null)]
    [InlineData("out_time=00:00:02.500000", null)]
    [InlineData("progress=end", null)]
    public void Progress_is_read_from_out_time(string line, double? expected) =>
        FfmpegPlan.Progress(line, TimeSpan.FromSeconds(10)).Should().Be(expected);

    [Fact]
    public void Progress_needs_a_duration() => FfmpegPlan.Progress("out_time_us=1", null).Should().BeNull();

    [Fact]
    public void Encoder_names_are_read_after_the_legend()
    {
        string[] lines =
        [
            "Encoders:",
            " V..... = Video",
            " A..... = Audio",
            " ------",
            " V....D libsvtav1            SVT-AV1(Scalable Video Technology for AV1) encoder (codec av1)",
            " A....D aac                  AAC (Advanced Audio Coding)",
        ];

        FfmpegPlan.ParseEncoders(lines).Should().BeEquivalentTo(["libsvtav1", "aac"]);
    }

    [Fact]
    public void Probe_json_is_read_with_rotation_frame_rate_and_cover_art()
    {
        const string json = """
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
                  "avg_frame_rate": "30000/1001", "bit_rate": "8000000",
                  "side_data_list": [ { "side_data_type": "Display Matrix", "rotation": -90 } ],
                  "disposition": { "attached_pic": 0 } },
                { "index": 1, "codec_type": "audio", "codec_name": "flac", "channels": 2, "bits_per_raw_sample": "24",
                  "avg_frame_rate": "0/0", "disposition": { "attached_pic": 0 } },
                { "index": 2, "codec_type": "video", "codec_name": "mjpeg", "width": 600, "height": 600,
                  "avg_frame_rate": "0/0", "disposition": { "attached_pic": 1 } }
              ],
              "format": { "duration": "12.500000", "bit_rate": "8200000" }
            }
            """;

        var info = MediaProbe.Parse(json);

        info.Duration.Should().Be(TimeSpan.FromSeconds(12.5));
        info.BitRate.Should().Be(8_200_000);
        info.Video.Should().BeEquivalentTo(new MediaTrack(0, "video", "h264", 1080, 1920, 30000 / 1001.0, 8_000_000));
        info.Audio.Should().ContainSingle().Which.Should().BeEquivalentTo(new MediaTrack(1, "audio", "flac", BitsPerSample: 24, Channels: 2));
        info.Streams[2].AttachedPicture.Should().BeTrue();
    }
}
