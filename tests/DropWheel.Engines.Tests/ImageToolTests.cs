using System.Text;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Tools;
using FluentAssertions;
using ImageMagick;

namespace DropWheel.Engines.Tests;

public sealed class ImageToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-tools-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Red on the left, blue on the right, so a turn shows where the halves went.</summary>
    private string Sample(string name = "photo.jpg", uint width = 64, uint height = 48, Action<MagickImage>? dress = null, uint quality = 92)
    {
        using var image = new MagickImage(MagickColors.Blue, width, height);
        image.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, (width / 2) - 1, height - 1));
        image.Quality = quality;
        dress?.Invoke(image);
        var path = Path.Combine(_dir, name);
        image.Write(path, FormatRegistry.FromPath(name)!.Id switch
        {
            "jpg" => MagickFormat.Jpeg,
            "png" => MagickFormat.Png,
            "webp" => MagickFormat.WebP,
            "gif" => MagickFormat.Gif,
            "bmp" => MagickFormat.Bmp,
            var other => throw new ArgumentException(other),
        });
        return path;
    }

    /// <summary>GPS position, camera, XMP, a comment and an ICC profile; turned when <paramref name="orientation"/> says so.</summary>
    private static Action<MagickImage> Tagged(OrientationType orientation = OrientationType.TopLeft) => image =>
    {
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "PhoneMaker");
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(31, 1), new Rational(14, 1), new Rational(0, 1)]);
        if (orientation != OrientationType.TopLeft)
        {
            exif.SetValue(ExifTag.Orientation, (ushort)orientation);
            image.Orientation = orientation;
        }

        image.SetProfile(exif);
        image.SetProfile(new XmpProfile(Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><secret/></x:xmpmeta>")));
        image.SetProfile(ColorProfiles.SRGB);
        image.SetAttribute("comment", "shot at home");
    };

    private static Task<ConversionResult> Run(IWheelOperation tool, params string[] sources) =>
        tool.RunAsync(new(sources, tool.Id, ""), new InlineProgress(_ => { }), CancellationToken.None);

    private static double Difference(string a, string b)
    {
        using var x = new MagickImage(a);
        using var y = new MagickImage(b);
        return x.Compare(y, ErrorMetric.Absolute);
    }

    [Fact]
    public async Task Strip_takes_the_position_out_of_a_jpeg_and_keeps_its_pixels_colours_and_turn()
    {
        var source = Sample(dress: Tagged(OrientationType.RightTop));
        using (var fixture = new MagickImage(source))
        {
            fixture.GetExifProfile()!.GetValue(ExifTag.GPSLatitude).Should().NotBeNull("the fixture must carry a position");
            fixture.GetXmpProfile().Should().NotBeNull();
        }

        var result = await Run(new StripMetadataTool(), source);

        result.Succeeded.Should().BeTrue(result.Error);
        var output = Path.Combine(_dir, "photo-无元数据.jpg");
        result.OutputPaths.Should().Equal(output);
        using var stripped = new MagickImage(output);
        var exif = stripped.GetExifProfile();
        exif!.Values.Select(v => v.Tag).Should().Equal([ExifTag.Orientation], "only the turn is kept");
        stripped.Orientation.Should().Be(OrientationType.RightTop);
        stripped.GetXmpProfile().Should().BeNull();
        stripped.GetAttribute("comment").Should().BeNull();
        stripped.GetColorProfile().Should().NotBeNull("the ICC profile changes how the colours show");
        Difference(source, output).Should().Be(0, "the pixels are not encoded again");
        new FileInfo(output).Length.Should().BeLessThan(new FileInfo(source).Length);
    }

    [Fact]
    public async Task Strip_drops_what_follows_the_end_of_a_jpeg()
    {
        var plain = File.ReadAllBytes(Sample("plain.jpg"));
        var second = File.ReadAllBytes(Sample("second.jpg", 16, 16, Tagged()));
        var source = Path.Combine(_dir, "phone.jpg");
        File.WriteAllBytes(source, [.. plain, .. second]);

        var result = await Run(new StripMetadataTool(), source);

        File.ReadAllBytes(result.OutputPaths[0]).Should().Equal(plain, "a phone's second image carries EXIF of its own");
    }

    [Fact]
    public async Task Strip_writes_nothing_for_a_jpeg_without_metadata()
    {
        var source = Sample();

        var result = await Run(new StripMetadataTool(), source);

        result.Succeeded.Should().BeTrue();
        result.Skipped.Should().BeTrue();
        result.Detail.Should().Be("没有可去除的元数据");
        Directory.GetFiles(_dir).Should().Equal(source);
    }

    [Fact]
    public async Task Strip_drops_png_text_and_time()
    {
        var source = Sample("shot.png", dress: image =>
        {
            image.SetAttribute("comment", "shot at home");
            image.SetProfile(new ExifProfile());
        });
        Encoding.Latin1.GetString(File.ReadAllBytes(source)).Should().Contain("tEXt");

        var result = await Run(new StripMetadataTool(), source);

        var output = File.ReadAllBytes(result.OutputPaths[0]);
        var text = Encoding.Latin1.GetString(output);
        text.Should().NotContain("tEXt").And.NotContain("zTXt").And.NotContain("iTXt").And.NotContain("eXIf").And.NotContain("tIME");
        text.Should().EndWith("IEND\xAE\x42\x60\x82");
        Difference(source, result.OutputPaths[0]).Should().Be(0);
    }

    [Fact]
    public async Task Strip_drops_webp_exif_and_xmp()
    {
        var source = Sample("photo.webp", dress: Tagged());
        using (var fixture = new MagickImage(source))
        {
            fixture.GetExifProfile().Should().NotBeNull("the fixture must carry EXIF");
        }

        var result = await Run(new StripMetadataTool(), source);

        var output = File.ReadAllBytes(result.OutputPaths[0]);
        BitConverter.ToUInt32(output, 4).Should().Be((uint)output.Length - 8);
        using var stripped = new MagickImage(result.OutputPaths[0]);
        stripped.GetExifProfile().Should().BeNull();
        stripped.GetXmpProfile().Should().BeNull();
        stripped.GetColorProfile().Should().NotBeNull();
        Difference(source, result.OutputPaths[0]).Should().Be(0);
    }

    [Fact]
    public async Task Strip_names_a_broken_file_and_writes_nothing()
    {
        var good = Sample("a.jpg", dress: Tagged());
        var bad = Path.Combine(_dir, "b.jpg");
        File.WriteAllBytes(bad, [0xFF, 0xD8, 0xFF, 0xE1, 0x7F, 0xFF, 0x00]);

        var result = await Run(new StripMetadataTool(), good, bad);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().StartWith("b.jpg");
        Directory.GetFiles(_dir).Should().BeEquivalentTo([good, bad]);
    }

    [Fact]
    public async Task A_batch_says_how_many_were_left_alone()
    {
        var tagged = Sample("a.jpg", dress: Tagged());
        var plain = Sample("b.jpg");

        var result = await Run(new StripMetadataTool(), tagged, plain);

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "a-无元数据.jpg"));
        result.Detail.Should().Be("另有 1 张没有可去除的元数据");
    }

    [Fact]
    public async Task Compress_lowers_jpeg_quality_and_keeps_the_metadata()
    {
        var source = Sample("noise.jpg", 400, 300, image => { image.AddNoise(NoiseType.Gaussian); Tagged()(image); }, quality: 97);

        var result = await Run(new CompressImageTool(), source);

        result.Succeeded.Should().BeTrue(result.Error);
        result.OutputPaths.Should().Equal(Path.Combine(_dir, "noise-压缩.jpg"));
        var (before, after) = (new FileInfo(source).Length, new FileInfo(result.OutputPaths[0]).Length);
        after.Should().BeLessThan(before);
        result.Detail.Should().Contain("→");
        using var compressed = new MagickImage(result.OutputPaths[0]);
        compressed.Quality.Should().Be(80);
        compressed.GetExifProfile()!.GetValue(ExifTag.GPSLatitude).Should().NotBeNull();
    }

    [Fact]
    public async Task Compress_keeps_nothing_that_is_not_smaller()
    {
        // The smallest PNG there is: Magick's own would add chunks and come out bigger.
        var source = Path.Combine(_dir, "dot.png");
        File.WriteAllBytes(source, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg=="));

        var result = await Run(new CompressImageTool(), source);

        result.Skipped.Should().BeTrue(result.Error);
        result.Detail.Should().Contain("没有变小");
        Directory.GetFiles(_dir).Should().Equal(source);
    }

    [Theory]
    [InlineData("pic.bmp", "*PNG 或 JPG*")]
    [InlineData("pic.gif", "*GIF*")]
    [InlineData("doc.pdf", "不支持 PDF")]
    public void Compress_says_why_a_format_is_not_covered(string name, string reason)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0]);

        var availability = new CompressImageTool().Check(new([Sample(), path], "tool.compress-image", ""));

        availability.IsAvailable.Should().BeFalse();
        availability.Reason.Should().Match(reason);
    }

    [Theory]
    [InlineData(64u, 48u, OrientationType.TopLeft, 32, 24)]
    [InlineData(64u, 48u, OrientationType.RightTop, 24, 32)]
    [InlineData(5u, 3u, OrientationType.TopLeft, 3, 2)]
    [InlineData(1u, 1u, OrientationType.TopLeft, 1, 1)]
    public async Task Half_size_is_named_with_the_new_size(uint width, uint height, OrientationType orientation, int w, int h)
    {
        var source = Sample(width: width, height: height, dress: orientation == OrientationType.TopLeft ? null : Tagged(orientation));

        var result = await Run(new HalfSizeTool(), source);

        result.OutputPaths.Should().Equal(Path.Combine(_dir, $"photo-{w}x{h}.jpg"));
        using var half = new MagickImage(result.OutputPaths[0]);
        (half.Width, half.Height).Should().Be(((uint)w, (uint)h));
        half.Orientation.Should().BeOneOf(OrientationType.TopLeft, OrientationType.Undefined);
    }

    [Fact]
    public async Task Half_size_keeps_every_frame_of_an_animation()
    {
        var source = Path.Combine(_dir, "anim.gif");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 40, 20));
            frames.Add(new MagickImage(MagickColors.Green, 40, 20));
            frames.Add(new MagickImage(MagickColors.Blue, 40, 20));
            frames.Write(source, MagickFormat.Gif);
        }

        var result = await Run(new HalfSizeTool(), source);

        using var half = new MagickImageCollection(result.OutputPaths[0]);
        half.Select(f => (f.Width, f.Height)).Should().Equal((20u, 10u), (20u, 10u), (20u, 10u));
    }

    [Fact]
    public async Task Rotate_right_puts_the_left_side_on_top()
    {
        var source = Sample("shot.png");

        var result = await Run(new RotateRightTool(), source);

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "shot-旋转.png"));
        using var turned = new MagickImage(result.OutputPaths[0]);
        (turned.Width, turned.Height).Should().Be((48u, 64u));
        var pixels = turned.GetPixels();
        pixels.GetPixel(24, 10).ToColor()!.Should().Be(MagickColors.Red);
        pixels.GetPixel(24, 54).ToColor()!.Should().Be(MagickColors.Blue);
    }

    [Fact]
    public async Task Rotate_applies_the_stored_turn_first()
    {
        var source = Sample(dress: Tagged(OrientationType.RightTop));

        var result = await Run(new RotateRightTool(), source);

        using var turned = new MagickImage(result.OutputPaths[0]);
        (turned.Width, turned.Height, turned.Orientation).Should().Be((64u, 48u, OrientationType.TopLeft), "shown 48×64, then turned once more");
    }

    [Fact]
    public void Image_tools_belong_on_the_image_wheel_only()
    {
        var tool = new HalfSizeTool();

        tool.AppliesTo(MediaKind.Image).Should().BeTrue();
        tool.AppliesTo(MediaKind.Pdf).Should().BeFalse();
        tool.Mode.Should().Be(WheelMode.Tools);
    }
}
