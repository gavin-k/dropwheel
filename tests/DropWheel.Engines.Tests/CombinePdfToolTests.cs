// PDFtoImage marks its API with the platforms it ships PDFium for; these tests run on all of them.
#pragma warning disable CA1416

using System.Text;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Engines.Tools;
using FluentAssertions;
using ImageMagick;

namespace DropWheel.Engines.Tests;

public sealed class CombinePdfToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-combine-").FullName;
    private readonly CombinePdfTool _tool = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>No DPI in the file, so a pixel is 0.75 pt on the page.</summary>
    private string Image(string name, uint width, uint height, MagickFormat format)
    {
        var path = Path.Combine(_dir, name);
        using var image = new MagickImage("gradient:red-blue", width, height);
        image.Write(path, format);
        return path;
    }

    private Task<ConversionResult> Run(params string[] sources) =>
        _tool.RunAsync(new(sources, _tool.Id, ""), new InlineProgress(_ => { }), CancellationToken.None);

    private static IList<System.Drawing.SizeF> PageSizes(string pdf)
    {
        using var input = File.OpenRead(pdf);
        return PDFtoImage.Conversion.GetPageSizes(input);
    }

    [Fact]
    public async Task Images_become_pages_in_file_name_order()
    {
        var ten = Image("scan 10.png", 192, 96, MagickFormat.Png);
        var two = Image("Scan 2.jpg", 48, 96, MagickFormat.Jpeg);
        var one = Image("scan 1.webp", 96, 48, MagickFormat.WebP);
        var progress = new List<double>();

        var result = await _tool.RunAsync(new([ten, two, one], _tool.Id, ""), new InlineProgress(progress.Add), CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Error);
        var pdf = Path.Combine(_dir, "scan 1-合并.pdf");
        result.OutputPaths.Should().Equal(pdf);
        result.Detail.Should().Be("3 张图片");
        PageSizes(pdf).Should().Equal(new System.Drawing.SizeF(72, 36), new System.Drawing.SizeF(36, 72), new System.Drawing.SizeF(144, 72));
        Encoding.Latin1.GetString(File.ReadAllBytes(pdf)).Should().Contain("/DCTDecode", "the JPEG goes in as it is and the WebP as JPEG");
        progress.Should().BeInAscendingOrder().And.EndWith(1.0);
    }

    [Fact]
    public async Task The_png_page_stays_lossless_next_to_a_webp()
    {
        var png = Image("a.png", 64, 48, MagickFormat.Png);
        var webp = Image("b.webp", 64, 48, MagickFormat.WebP);

        var result = await Run(png, webp);

        var text = Encoding.Latin1.GetString(File.ReadAllBytes(result.OutputPaths[0]));
        text.Split("/DCTDecode").Length.Should().Be(2, "only the WebP page is stored as JPEG");
    }

    [Fact]
    public async Task A_broken_image_is_named_and_nothing_is_written()
    {
        var good = Image("a.png", 64, 48, MagickFormat.Png);
        var bad = Path.Combine(_dir, "b.png");
        File.WriteAllBytes(bad, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]);

        var result = await Run(good, bad);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().StartWith("b.png");
        Directory.GetFiles(_dir).Should().BeEquivalentTo([good, bad]);
    }

    [Fact]
    public void One_image_is_not_enough()
    {
        var one = Image("a.png", 8, 8, MagickFormat.Png);

        var availability = _tool.Check(new([one], _tool.Id, ""));

        availability.IsAvailable.Should().BeFalse();
        availability.Cause.Should().Be(UnavailableCause.SelectionShape);
        _tool.Check(new([one, Image("b.png", 8, 8, MagickFormat.Png)], _tool.Id, "")).IsAvailable.Should().BeTrue();
        _tool.Check(new([one, Path.Combine(_dir, "c.svg")], _tool.Id, "")).Reason.Should().Be("不支持 SVG");
    }
}
