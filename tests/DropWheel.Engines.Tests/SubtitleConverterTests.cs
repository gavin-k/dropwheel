using System.Text;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Engines.Subtitle;
using FluentAssertions;

namespace DropWheel.Engines.Tests;

public sealed class SubtitleConverterTests : IDisposable
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-subtitle-").FullName;
    private readonly SubtitleConverter _converter = new();

    static SubtitleConverterTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Sample(string name, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, (encoding ?? new UTF8Encoding(false)).GetBytes(text));
        return path;
    }

    private async Task<string> Convert(string source, string target)
    {
        var result = await _converter.RunAsync(new([source], target, ""), new InlineProgress(_ => { }), CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.Error);
        var bytes = File.ReadAllBytes(result.OutputPaths.Single());
        bytes.Should().StartWith(Bom, "outputs are UTF-8 with a BOM");
        return Encoding.UTF8.GetString(bytes, Bom.Length, bytes.Length - Bom.Length);
    }

    private const string Srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>你好</i>\r\n世界\r\n\r\n2\r\n00:01:03,040 --> 00:01:05,000\r\nA & B\r\n";

    [Fact]
    public void Every_text_format_converts_to_every_other()
    {
        _converter.Edges.Should().HaveCount(20);
        _converter.Edges.Should().Contain(new ConversionEdge("srt", "vtt")).And.Contain(new ConversionEdge("lrc", "ass"));
    }

    [Fact]
    public async Task Gbk_srt_becomes_utf8_vtt()
    {
        var source = Sample("movie.srt", Srt, Encoding.GetEncoding("GBK"));

        var vtt = await Convert(source, "vtt");

        vtt.Should().Be("WEBVTT\r\n\r\n00:00:01.000 --> 00:00:02.500\r\n<i>你好</i>\r\n世界\r\n\r\n00:01:03.040 --> 00:01:05.000\r\nA &amp; B\r\n\r\n");
    }

    [Fact]
    public async Task Utf16_input_is_read_through_its_bom()
    {
        var source = Path.Combine(_dir, "movie.srt");
        File.WriteAllBytes(source, [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Srt)]);

        (await Convert(source, "srt")).Should().Contain("<i>你好</i>\r\n世界");
    }

    [Fact]
    public async Task Srt_survives_a_round_trip_through_ass()
    {
        var ass = await Convert(Sample("movie.srt", Srt), "ass");
        ass.Should().Contain("Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,{\\i1}你好{\\i0}\\N世界\r\n");
        File.Delete(Path.Combine(_dir, "movie.srt"));

        var srt = await Convert(Path.Combine(_dir, "movie.ass"), "srt");

        srt.Should().Be(Srt.Replace("A & B\r\n", "A & B\r\n\r\n"));
    }

    [Fact]
    public async Task Ssa_uses_the_v4_layout()
    {
        var ssa = await Convert(Sample("movie.srt", Srt), "ssa");

        ssa.Should().Contain("ScriptType: v4.00\r\n").And.Contain("[V4 Styles]")
            .And.Contain("Dialogue: Marked=0,0:01:03.04,0:01:05.00,Default,,0,0,0,,A & B\r\n");
        File.Delete(Path.Combine(_dir, "movie.srt"));
        (await Convert(Path.Combine(_dir, "movie.ssa"), "srt")).Should().Contain("00:01:03,040 --> 00:01:05,000\r\nA & B");
    }

    [Fact]
    public async Task Vtt_markup_is_reduced_to_basic_tags()
    {
        const string vtt = """
            WEBVTT - with a title

            NOTE a comment
            spanning lines

            intro
            00:01.000 --> 00:02.000 align:start position:10%
            <v Roger><i.loud>Hi</i> <c.yellow>there</c> &amp; <00:01.500>you</v>

            00:00:03.000 --> 00:00:04.000
            <b>bold
            """;

        var srt = await Convert(Sample("talk.vtt", vtt), "srt");

        srt.Should().Be("1\r\n00:00:01,000 --> 00:00:02,000\r\n<i>Hi</i> there & you\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\n<b>bold</b>\r\n\r\n");
    }

    [Fact]
    public async Task Vtt_output_escapes_text_that_looks_like_markup()
    {
        var source = Sample("odd.srt", "1\n00:00:01,000 --> 00:00:02,000\na <b>x</b> -> b < c\n");

        (await Convert(source, "vtt")).Should().Contain("a <b>x</b> -&gt; b &lt; c\r\n");
    }

    [Fact]
    public async Task Srt_without_blank_lines_between_cues_still_parses()
    {
        var source = Sample("tight.srt", "1\n00:00:01,000 --> 00:00:02,000\none\n2\n00:00:03,000 --> 00:00:04,000\ntwo\n");

        var srt = await Convert(source, "vtt");

        srt.Should().Contain("00:00:01.000 --> 00:00:02.000\r\none\r\n\r\n00:00:03.000 --> 00:00:04.000\r\ntwo\r\n");
    }

    [Fact]
    public async Task Ass_reads_columns_from_the_format_line_and_drops_overrides()
    {
        const string ass = """
            [Script Info]
            ScriptType: v4.00+

            [V4+ Styles]
            Format: Name, Fontname, Fontsize
            Style: Default,Arial,20

            [Events]
            Format: Layer, Start, End, Style, Actor, MarginL, MarginR, MarginV, Effect, Text
            Comment: 0,0:00:00.00,0:00:09.00,Default,,0,0,0,,not shown
            Dialogue: 0,0:00:05.00,0:00:06.00,Default,,0,0,0,,{\pos(10,20)\i1}later, with commas{\i0}
            Dialogue: 0,0:00:01.50,0:00:02.25,Default,Bob,0,0,0,fade,top\Nbottom\hend{\p1}m 0 0 l 10 10{\p0}
            Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,{\p1}m 0 0 l 10 10{\p0}
            """;

        var srt = await Convert(Sample("show.ass", ass), "srt");

        srt.Should().Be("1\r\n00:00:01,500 --> 00:00:02,250\r\ntop\r\nbottom end\r\n\r\n2\r\n00:00:05,000 --> 00:00:06,000\r\n<i>later, with commas</i>\r\n\r\n");
    }

    [Fact]
    public async Task Lrc_end_times_come_from_the_next_line()
    {
        const string lrc = """
            [ti:Song]
            [ar:Someone]
            [offset:+500]
            [00:10.00][01:00.00]chorus
            [00:20.50]<00:20.50>verse <00:21.00>words
            [00:25.00]
            [01:30.5]last
            """;

        var srt = await Convert(Sample("song.lrc", lrc), "srt");

        srt.Should().Be(
            "1\r\n00:00:09,500 --> 00:00:20,000\r\nchorus\r\n\r\n" +
            "2\r\n00:00:20,000 --> 00:00:24,500\r\nverse words\r\n\r\n" +
            "3\r\n00:00:59,500 --> 00:01:30,000\r\nchorus\r\n\r\n" +
            "4\r\n00:01:30,000 --> 00:01:35,000\r\nlast\r\n\r\n");
    }

    [Fact]
    public async Task Lrc_output_merges_lines_and_marks_gaps()
    {
        var lrc = await Convert(Sample("movie.srt", Srt), "lrc");

        lrc.Should().Be("[00:01.00]你好 世界\r\n[00:02.50]\r\n[01:03.04]A & B\r\n[01:05.00]\r\n");
    }

    [Fact]
    public async Task A_file_without_cues_fails_the_whole_batch()
    {
        var good = Sample("a.srt", Srt);
        var empty = Sample("b.srt", "just some text\n");

        var result = await _converter.RunAsync(new([good, empty], "vtt", ""), new InlineProgress(_ => { }), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("b.srt");
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().BeEquivalentTo(["a.srt", "b.srt"]);
    }

    [Theory]
    [InlineData("a.srt", "vtt", null)]
    [InlineData("a.ass", "srt", "样式")]
    [InlineData("a.ass", "ssa", "样式")]
    [InlineData("a.lrc", "srt", "结束时间")]
    [InlineData("a.srt", "lrc", "合并")]
    public void Estimate_names_what_gets_lost(string source, string target, string? reason)
    {
        var estimate = _converter.Estimate(new([source], target, ""));

        if (reason is null)
        {
            estimate.Should().BeNull();
        }
        else
        {
            estimate!.Level.Should().Be(QualityLevel.Medium);
            estimate.Reasons.Should().ContainMatch($"*{reason}*");
        }
    }
}
