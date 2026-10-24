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

    [Fact]
    public void Edges_cover_m0_formats_only()
    {
        _converter.Edges.Should().Contain(new ConversionEdge("jpg", "png"))
            .And.Contain(new ConversionEdge("png", "jpg"))
            .And.NotContain(e => e.FromFormatId == "heic" || e.ToFormatId == "ico" || e.FromFormatId == e.ToFormatId);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
