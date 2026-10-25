using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Media;
using DropWheel.Engines.Tools;
using FluentAssertions;

namespace DropWheel.Engines.Tests;

/// <summary>
/// Runs the real ffmpeg on the PATH, as <see cref="FfmpegConverterTests"/> does. Compressing needs an H.264 encoder,
/// which on Windows is Media Foundation's; a Windows runner without it may only fail with the message for that.
/// </summary>
public sealed class MediaToolTests : IDisposable
{
    private const string NoH264 = "系统缺少 H.264 编码器（Windows N 版需安装媒体功能包）";

    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-mediatools-").FullName;
    private readonly FfmpegConverter _ffmpeg = new();

    private static FfmpegTools Tools => FfmpegLocator.Find() ?? throw new InvalidOperationException("ffmpeg and ffprobe must be on the PATH for these tests");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> Sample(string name, params string[] args)
    {
        var path = Path.Combine(_dir, name);
        var result = await new SidecarProcess(Tools.Ffmpeg).RunAsync(["-hide_banner", "-loglevel", "error", .. args, "-y", "file:" + path], null, CancellationToken.None);
        result.ExitCode.Should().Be(0, string.Join('\n', result.StderrTail));
        return path;
    }

    /// <summary>A test picture in MPEG-4 Part 2 with a 440 Hz tone, or no sound when <paramref name="audio"/> is null.</summary>
    private Task<string> Video(string name, string? audio = "aac", string size = "320x240", int quality = 5, string muxer = "matroska") => Sample(name,
    [
        "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=25:duration=2",
        .. audio is null ? Array.Empty<string>() : ["-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", audio],
        "-c:v", "mpeg4", "-q:v", quality.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", muxer,
    ]);

    private static Task<ConversionResult> Run(IWheelOperation tool, string[] sources, List<double>? progress = null) =>
        tool.RunAsync(new(sources, tool.Id, ""), new InlineProgress(p => progress?.Add(p)), CancellationToken.None);

    private static Task<MediaInfo> Probe(string path) => MediaProbe.ProbeAsync(Tools.Ffprobe, path, CancellationToken.None);

    [Theory]
    [InlineData("aac", "clip.m4a", "aac")]
    [InlineData("flac", "clip.flac", "flac")]
    [InlineData("pcm_s16be", "clip.wav", "pcm_s16le")]
    [InlineData("ac3", "clip.m4a", "aac")]
    public async Task Audio_is_taken_out_as_it_is_where_an_audio_format_holds_it(string codec, string output, string expected)
    {
        var source = await Video(codec == "pcm_s16be" ? "clip.mov" : "clip.mkv", codec, muxer: codec == "pcm_s16be" ? "mov" : "matroska");
        var progress = new List<double>();

        var result = await Run(new ExtractAudioTool(_ffmpeg), [source], progress);

        result.Succeeded.Should().BeTrue(result.Error);
        result.OutputPaths.Should().Equal(Path.Combine(_dir, output));
        var info = await Probe(result.OutputPaths[0]);
        info.Video.Should().BeNull();
        info.Audio.Should().ContainSingle().Which.Codec.Should().Be(expected);
        info.Duration!.Value.TotalSeconds.Should().BeApproximately(2, 0.2);
        progress.Should().BeInAscendingOrder().And.EndWith(1.0);
    }

    [Fact]
    public async Task A_video_without_sound_has_no_audio_to_take_out()
    {
        var silent = await Video("silent.mkv", audio: null);
        var loud = await Video("loud.mkv");

        var alone = await Run(new ExtractAudioTool(_ffmpeg), [silent]);
        var batch = await Run(new ExtractAudioTool(_ffmpeg), [silent, loud]);

        alone.Skipped.Should().BeTrue();
        alone.Detail.Should().Be("没有音轨");
        batch.Succeeded.Should().BeTrue(batch.Error);
        batch.OutputPaths.Should().ContainSingle().Which.Should().EndWith(".m4a");
        batch.Detail.Should().Be("另有 1 个没有音轨");
    }

    [Fact]
    public async Task Muting_copies_the_picture_and_drops_the_sound()
    {
        var source = await Video("clip.mkv");

        var result = await Run(new MuteVideoTool(_ffmpeg), [source]);

        result.Succeeded.Should().BeTrue(result.Error);
        result.OutputPaths.Should().Equal(Path.Combine(_dir, "clip-无声.mkv"));
        var info = await Probe(result.OutputPaths[0]);
        info.Audio.Should().BeEmpty();
        info.Video.Should().BeEquivalentTo(new { Codec = "mpeg4", Width = 320, Height = 240 });
        info.Duration!.Value.TotalSeconds.Should().BeApproximately(2, 0.2);
    }

    [Fact]
    public async Task A_video_without_sound_is_left_as_it_is()
    {
        var silent = await Video("silent.mp4", audio: null, muxer: "mp4");

        var result = await Run(new MuteVideoTool(_ffmpeg), [silent]);

        result.Skipped.Should().BeTrue();
        result.Detail.Should().Be("本来就没有声音");
        Directory.GetFiles(_dir).Should().Equal(silent);
    }

    [Fact]
    public async Task Sound_without_a_picture_cannot_be_muted()
    {
        var song = await Sample("song.mkv", "-f", "lavfi", "-i", "sine=duration=1", "-c:a", "aac", "-f", "matroska");

        var result = await Run(new MuteVideoTool(_ffmpeg), [song]);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("song.mkv 无法处理：没有视频画面");
    }

    [Fact]
    public void Rmvb_cannot_be_muted_as_it_is()
    {
        var path = Path.Combine(_dir, "old.rmvb");
        File.WriteAllBytes(path, [0]);

        new MuteVideoTool(_ffmpeg).Check(new([path], "tool.mute", "")).Reason.Should().Be("RMVB 不能原样去掉声音，先转成 MP4");
        new CompressVideoTool(_ffmpeg).Check(new([path], "tool.compress-video", "")).IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Without_ffmpeg_the_tools_are_greyed_with_the_reason()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_dir, "no-ffmpeg")).FullName;
        var tool = new CompressVideoTool(new FfmpegConverter(empty));

        var availability = tool.Check(new([Path.Combine(_dir, "a.mp4")], tool.Id, ""));

        availability.IsAvailable.Should().BeFalse();
        availability.Cause.Should().Be(UnavailableCause.MissingOptionalModule);
    }

    [Fact]
    public async Task A_big_video_is_compressed_to_a_smaller_mp4()
    {
        var source = await Video("clip.mkv", size: "1280x720", quality: 2);

        var result = await Run(new CompressVideoTool(_ffmpeg), [source]);

        if (OperatingSystem.IsWindows() && result.Error == NoH264)
        {
            return;
        }

        result.Succeeded.Should().BeTrue(result.Error);
        var output = Path.Combine(_dir, "clip-压缩.mp4");
        result.OutputPaths.Should().Equal(output);
        new FileInfo(output).Length.Should().BeLessThan(new FileInfo(source).Length);
        result.Detail.Should().MatchRegex(@"^\d+(\.\d)? [KM]B → \d+(\.\d)? [KM]?B$");
        var info = await Probe(output);
        info.Video.Should().BeEquivalentTo(new { Codec = "h264", Width = 1280, Height = 720 });
        info.Audio.Should().ContainSingle().Which.Codec.Should().Be("aac");
    }

    [Fact]
    public async Task A_video_that_does_not_get_smaller_is_left_as_it_is()
    {
        var source = await Video("tiny.mkv", audio: null, size: "64x48", quality: 31);

        var result = await Run(new CompressVideoTool(_ffmpeg), [source]);

        if (OperatingSystem.IsWindows() && result.Error == NoH264)
        {
            return;
        }

        result.Succeeded.Should().BeTrue(result.Error);
        result.Skipped.Should().BeTrue();
        result.Detail.Should().Be("已经很小，压缩后没有变小");
        Directory.GetFiles(_dir).Should().Equal(source);
    }
}
