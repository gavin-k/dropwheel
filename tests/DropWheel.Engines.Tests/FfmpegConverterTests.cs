using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Engines.Media;
using FluentAssertions;

namespace DropWheel.Engines.Tests;

/// <summary>
/// Runs the real ffmpeg found on the PATH (CI installs one; see build.yml). The samples are generated with lavfi and
/// avoid H.264, whose Media Foundation encoder Windows Server runners may lack.
/// </summary>
public sealed class FfmpegConverterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-ffmpeg-").FullName;
    private readonly FfmpegConverter _converter = new();

    private static FfmpegTools Tools => FfmpegLocator.Find() ?? throw new InvalidOperationException("ffmpeg and ffprobe must be on the PATH for these tests");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Generates a sample with ffmpeg's lavfi sources.</summary>
    private async Task<string> Sample(string name, params string[] args)
    {
        var path = Path.Combine(_dir, name);
        var result = await new SidecarProcess(Tools.Ffmpeg).RunAsync(["-hide_banner", "-loglevel", "error", .. args, "-y", "file:" + path], null, CancellationToken.None);
        result.ExitCode.Should().Be(0, string.Join('\n', result.StderrTail));
        return path;
    }

    private Task<string> Video(string name = "clip.mkv", string size = "320x240", int seconds = 2, string codec = "mpeg4") => Sample(name,
        "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=25:duration={seconds}",
        "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
        "-c:v", codec, "-q:v", "5", "-c:a", "aac", "-f", "matroska");

    private Task<string> Wav(string name, string codec = "pcm_s16le", int channels = 2) => Sample(name,
        "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-ac", channels.ToString(), "-c:a", codec, "-f", "wav");

    private async Task<string> Convert(string source, string target, List<double>? progress = null)
    {
        var result = await _converter.RunAsync(new([source], target, ""), new InlineProgress(p => progress?.Add(p)), CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.Error);
        return result.OutputPaths.Single();
    }

    private static Task<MediaInfo> Probe(string path) => MediaProbe.ProbeAsync(Tools.Ffprobe, path, CancellationToken.None);

    private IEnumerable<string> Leftovers() =>
        Directory.EnumerateFiles(_dir).Where(f => f.EndsWith(OutputFiles.TempSuffix, StringComparison.Ordinal));

    [Fact]
    public void Edges_cover_video_and_audio_but_never_write_input_only_formats()
    {
        var edges = _converter.Edges.ToList();

        edges.Should().HaveCount(143).And.OnlyHaveUniqueItems();
        edges.Should().Contain([new("rmvb", "mp4"), new("mkv", "gif"), new("mov", "mp3"), new("amr", "mp3")]);
        edges.Should().NotContain(e => e.ToFormatId == "rmvb" || e.ToFormatId == "amr" || e.FromFormatId == e.ToFormatId);
    }

    [Fact]
    public void Without_ffmpeg_the_items_are_greyed_with_the_reason()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_dir, "no-ffmpeg")).FullName;

        var availability = new FfmpegConverter(empty).Check(new([Path.Combine(_dir, "a.mp4")], "mkv", ""));

        availability.IsAvailable.Should().BeFalse();
        availability.Reason.Should().Contain("ffmpeg");
        availability.Cause.Should().Be(UnavailableCause.MissingOptionalModule);
    }

    [Fact]
    public async Task Mkv_to_mp4_copies_the_streams_and_reports_progress()
    {
        var source = await Video();
        var progress = new List<double>();

        var output = await Convert(source, "mp4", progress);

        output.Should().Be(Path.Combine(_dir, "clip.mp4"));
        var info = await Probe(output);
        info.Video!.Codec.Should().Be("mpeg4");
        info.Audio.Should().ContainSingle().Which.Codec.Should().Be("aac");
        info.Duration!.Value.TotalSeconds.Should().BeApproximately(2, 0.2);
        progress.Should().NotBeEmpty().And.BeInAscendingOrder();
        progress[^1].Should().Be(1);
    }

    [Fact]
    public async Task Chinese_named_wav_becomes_mp3()
    {
        var source = await Wav("会议 录音.wav");

        var output = await Convert(source, "mp3");

        output.Should().Be(Path.Combine(_dir, "会议 录音.mp3"));
        var head = new byte[3];
        await using (var f = File.OpenRead(output))
        {
            await f.ReadExactlyAsync(head);
        }

        (head is [(byte)'I', (byte)'D', (byte)'3'] or [0xFF, 0xFB or 0xF3 or 0xF2, _]).Should().BeTrue("an MP3 starts with an ID3 tag or a frame sync");
        (await Probe(output)).Audio.Single().Codec.Should().Be("mp3");
    }

    [Fact]
    public async Task Video_audio_track_is_extracted()
    {
        var output = await Convert(await Video(), "m4a");

        var info = await Probe(output);
        info.Video.Should().BeNull();
        info.Audio.Single().Codec.Should().Be("aac");
    }

    [Fact]
    public async Task Twenty_four_bit_wav_stays_twenty_four_bit_in_flac()
    {
        var output = await Convert(await Wav("hi-res.wav", "pcm_s24le"), "flac");

        (await Probe(output)).Audio.Single().BitsPerSample.Should().Be(24);
    }

    [Fact]
    public async Task Surround_wav_is_downmixed_for_mp3()
    {
        var output = await Convert(await Wav("surround.wav", channels: 6), "mp3");

        (await Probe(output)).Audio.Single().Channels.Should().Be(2);
    }

    [Fact]
    public async Task Video_becomes_a_gif()
    {
        var output = await Convert(await Video(), "gif");

        var head = new byte[6];
        await using (var f = File.OpenRead(output))
        {
            await f.ReadExactlyAsync(head);
        }

        System.Text.Encoding.ASCII.GetString(head).Should().Be("GIF89a");
        (await Probe(output)).Video!.Width.Should().Be(320);
    }

    [Fact]
    public async Task Odd_sized_video_is_reencoded_at_even_size_and_named_with_it()
    {
        var source = await Sample("odd.mkv", "-f", "lavfi", "-i", "testsrc2=size=321x241:rate=10:duration=1", "-c:v", "ffv1", "-f", "matroska");

        var result = await _converter.RunAsync(new([source], "avi", "") { Naming = "{name}_{w}x{h}" }, new InlineProgress(_ => { }), CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Error);
        result.OutputPaths.Should().Equal(Path.Combine(_dir, "odd_320x240.avi"));
        var video = (await Probe(result.OutputPaths[0])).Video!;
        (video.Codec, video.Width, video.Height).Should().Be(("mpeg4", 320, 240));
    }

    [Fact]
    public async Task Webm_is_encoded_with_an_open_codec()
    {
        var output = await Convert(await Video(seconds: 1), "webm");

        var info = await Probe(output);
        info.Video!.Codec.Should().BeOneOf("av1", "vp9");
        info.Audio.Single().Codec.Should().Be("opus");
    }

    [Fact]
    public async Task Damaged_file_fails_with_its_name_and_leaves_nothing_behind()
    {
        var good = await Video("a.mkv", seconds: 1);
        var broken = Path.Combine(_dir, "b.mkv");
        await File.WriteAllBytesAsync(broken, new byte[4096]);

        var result = await _converter.RunAsync(new([good, broken], "mp4", ""), new InlineProgress(_ => { }), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("b.mkv");
        File.Exists(Path.Combine(_dir, "a.mp4")).Should().BeFalse("a batch is all or nothing");
        Leftovers().Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_stops_ffmpeg_and_removes_the_partial_output()
    {
        var source = await Video("long.mkv", "640x360", seconds: 60);
        using var cts = new CancellationTokenSource();

        var act = () => _converter.RunAsync(new([source], "webm", ""), new InlineProgress(p =>
        {
            if (p > 0)
            {
                cts.Cancel();
            }
        }), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Leftovers().Should().BeEmpty();
        File.Exists(Path.Combine(_dir, "long.webm")).Should().BeFalse();
    }
}
