using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Engines.Image;
using FluentAssertions;
using ImageMagick;

namespace DropWheel.Engines.Tests;

public sealed class MagickImageConverterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-engines-").FullName;
    private readonly MagickImageConverter _converter = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Sample(string name = "photo.jpg", uint width = 64, uint height = 48, OrientationType orientation = OrientationType.TopLeft)
    {
        using var image = new MagickImage("gradient:red-blue", width, height);
        if (orientation != OrientationType.TopLeft)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)orientation);
            image.SetProfile(exif);
            image.Orientation = orientation; // the writer syncs the EXIF tag from this
        }

        var path = Path.Combine(_dir, name);
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    private Task<ConversionResult> Run(ConversionRequest request, List<double>? progress = null) =>
        _converter.RunAsync(request, new InlineProgress(p => progress?.Add(p)), CancellationToken.None);

    [Theory]
    [InlineData("png", MagickFormat.Png)]
    [InlineData("webp", MagickFormat.WebP)]
    [InlineData("avif", MagickFormat.Avif)]
    [InlineData("gif", MagickFormat.Gif)]
    [InlineData("bmp", MagickFormat.Bmp)]
    [InlineData("tiff", MagickFormat.Tiff)]
    public async Task Jpg_converts_next_to_the_source(string target, MagickFormat expected)
    {
        var source = Sample();
        var progress = new List<double>();

        var result = await Run(new([source], target, ""), progress);

        result.Succeeded.Should().BeTrue(result.Error);
        var output = Path.Combine(_dir, $"photo.{target}");
        result.OutputPaths.Should().Equal(output);
        var info = new MagickImageInfo(output);
        info.Format.Should().BeOneOf(expected, expected == MagickFormat.Tiff ? MagickFormat.Tif : expected);
        (info.Width, info.Height).Should().Be((64u, 48u));
        progress.Should().Equal(1.0);
        Directory.GetFiles(_dir).Should().HaveCount(2, "no temporary file is left behind");
    }

    [Fact]
    public async Task Png_converts_back_to_jpg()
    {
        var png = (await Run(new([Sample()], "png", ""))).OutputPaths[0];
        File.Delete(Path.Combine(_dir, "photo.jpg"));

        var result = await Run(new([png], "jpg", ""));

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "photo.jpg"));
        new MagickImageInfo(result.OutputPaths[0]).Format.Should().Be(MagickFormat.Jpeg);
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("bmp")]
    public async Task Transparent_pixels_come_out_white_where_the_format_has_no_alpha(string target)
    {
        var source = Path.Combine(_dir, "logo.png");
        using (var image = new MagickImage(MagickColors.Transparent, 40, 20))
        {
            image.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 19, 19));
            image.Write(source, MagickFormat.Png);
        }

        var result = await Run(new([source], target, ""));

        result.Succeeded.Should().BeTrue(result.Error);
        using var output = new MagickImage(result.OutputPaths[0]);
        var pixels = output.GetPixels();
        output.HasAlpha.Should().BeFalse();
        var (white, red) = (pixels.GetPixel(30, 10).ToColor()!, pixels.GetPixel(10, 10).ToColor()!);
        new[] { white.R, white.G, white.B }.Should().OnlyContain(c => c > 0xF000, "the transparent half is white, not black");
        (red.R > 0xF000, red.G < 0x1000, red.B < 0x1000).Should().Be((true, true, true));
    }

    [Fact]
    public async Task Transparency_is_kept_where_the_format_has_it()
    {
        var source = Path.Combine(_dir, "logo.png");
        using (var image = new MagickImage(MagickColors.Transparent, 40, 20))
        {
            image.Write(source, MagickFormat.Png);
        }

        var result = await Run(new([source], "webp", ""));

        using var output = new MagickImage(result.OutputPaths[0]);
        output.HasAlpha.Should().BeTrue();
        output.GetPixels().GetPixel(5, 5).ToColor()!.A.Should().Be(0);
    }

    [Fact]
    public async Task Exif_orientation_is_applied()
    {
        var source = Sample(orientation: OrientationType.RightTop);

        using (var fixture = new MagickImage(source))
        {
            fixture.Orientation.Should().Be(OrientationType.RightTop, "the fixture must carry the EXIF tag");
        }

        var result = await Run(new([source], "png", ""));

        var info = new MagickImageInfo(result.OutputPaths[0]);
        (info.Width, info.Height).Should().Be((48u, 64u));
    }

    [Fact]
    public async Task Existing_output_is_kept_and_a_number_is_added()
    {
        var source = Sample();
        var existing = Path.Combine(_dir, "photo.png");
        await File.WriteAllTextAsync(existing, "keep me");

        var result = await Run(new([source], "png", ""));

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "photo (1).png"));
        (await File.ReadAllTextAsync(existing)).Should().Be("keep me");
    }

    [Fact]
    public async Task Skip_policy_writes_nothing_when_the_output_exists()
    {
        var source = Sample();
        await File.WriteAllTextAsync(Path.Combine(_dir, "photo.png"), "keep me");

        var result = await Run(new([source], "png", "") { Conflict = ConflictPolicy.Skip });

        result.Succeeded.Should().BeTrue();
        result.OutputPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task Output_directory_and_naming_template_are_honoured()
    {
        var source = Sample();
        var outDir = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;

        var result = await Run(new([source], "webp", outDir) { Naming = "{name}_{w}x{h}" });

        result.OutputPaths.Should().Equal(Path.Combine(outDir, "photo_64x48.webp"));
    }

    [Fact]
    public async Task Batch_reports_progress_per_file()
    {
        var a = Sample("a.jpg");
        var b = Sample("b.jpg");
        var progress = new List<double>();

        var result = await Run(new([a, b], "png", ""), progress);

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "a.png"), Path.Combine(_dir, "b.png"));
        progress.Should().Equal(0.5, 1.0);
    }

    [Fact]
    public async Task Corrupt_source_fails_and_removes_outputs_of_the_same_run()
    {
        var good = Sample("a.jpg");
        var bad = Path.Combine(_dir, "b.jpg");
        await File.WriteAllBytesAsync(bad, [0xFF, 0xD8, 0x00, 0x01, 0x02]);

        var result = await Run(new([good, bad], "png", ""));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("b.jpg");
        result.OutputPaths.Should().BeEmpty();
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().BeEquivalentTo("a.jpg", "b.jpg");
    }

    [Fact]
    public async Task Canceled_before_start_writes_nothing()
    {
        var source = Sample();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var run = () => _converter.RunAsync(new([source], "png", ""), new InlineProgress(_ => { }), cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(_dir).Should().HaveCount(1);
    }

    [Fact]
    public void Heic_output_is_reported_as_missing_codec()
    {
        var availability = _converter.Check(new(["x.jpg"], "heic", ""));

        availability.IsAvailable.Should().BeFalse();
        availability.Cause.Should().Be(UnavailableCause.MissingSystemCodec);
    }

    [Theory]
    [InlineData("photo.jpg", "png", QualityLevel.High, "*压缩痕迹*")]
    [InlineData("photo.webp", "tiff", QualityLevel.High, "*压缩痕迹*")]
    [InlineData("shot.png", "jpg", QualityLevel.Medium, "*透明部分变成白色*")]
    [InlineData("shot.png", "bmp", QualityLevel.High, "*透明部分变成白色*")]
    [InlineData("anim.gif", "png", QualityLevel.High, "只取第一帧")]
    [InlineData("anim.gif", "jpg", QualityLevel.Medium, "只取第一帧")]
    [InlineData("photo.jpg", "webp", null, null)]
    [InlineData("shot.png", "gif", QualityLevel.Medium, "*256 色*")]
    public void Estimates_say_what_the_target_format_loses(string source, string target, QualityLevel? level, string? reason)
    {
        var estimate = _converter.Estimate(new([source], target, ""));

        estimate?.Level.Should().Be(level);
        if (reason is null)
        {
            estimate.Should().BeNull();
        }
        else
        {
            estimate!.Reasons.Should().ContainMatch(reason);
        }
    }

    [Fact]
    public void Edges_cover_m0_formats_only()
    {
        _converter.Edges.Should().Contain(new ConversionEdge("jpg", "png"))
            .And.Contain(new ConversionEdge("png", "jpg"))
            .And.NotContain(e => e.FromFormatId == "heic" || e.ToFormatId == "ico" || e.FromFormatId == e.ToFormatId);
    }
}
