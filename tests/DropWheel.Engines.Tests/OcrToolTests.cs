// PDFtoImage marks its API with the platforms it ships PDFium for; these tests run on all of them.
#pragma warning disable CA1416

using System.Text;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Engines.Ocr;
using DropWheel.Engines.Tools;
using FluentAssertions;
using ImageMagick;

namespace DropWheel.Engines.Tests;

public sealed class OcrToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-ocr-").FullName;
    private readonly FakeRecognizer _recognizer = new();
    private readonly OcrTool _tool;

    public OcrToolTests() => _tool = new(_recognizer);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Image(string name, uint width, uint height, MagickFormat format, string color = "red")
    {
        var path = Path.Combine(_dir, name);
        using var image = new MagickImage(color == "none" ? MagickColors.None : new MagickColor(color), width, height);
        image.Write(path, format);
        return path;
    }

    private Task<ConversionResult> Run(params string[] sources) =>
        _tool.RunAsync(new(sources, _tool.Id, ""), new InlineProgress(_ => { }), CancellationToken.None);

    private static string Text(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes.Take(3).Should().Equal([0xEF, 0xBB, 0xBF], "Notepad on an older Windows needs the BOM to read UTF-8");
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
    }

    [Fact]
    public async Task The_text_of_an_image_goes_into_a_txt_next_to_it()
    {
        var png = Image("note.png", 64, 48, MagickFormat.Png);
        _recognizer.Pages.Enqueue([["DropWheel", "2026"], ["第", "二", "行"]]);

        var result = await Run(png);

        result.Succeeded.Should().BeTrue(result.Error);
        result.OutputPaths.Should().Equal(Path.Combine(_dir, "note.txt"));
        Text(result.OutputPaths[0]).Should().Be("DropWheel 2026\r\n第二行\r\n");
        result.Detail.Should().Be("认出 2 行，识别语言 测试语言");
        _recognizer.Seen.Should().Equal((64, 48));
    }

    [Theory]
    [InlineData(new[] { "使用", "DropWheel", "转换" }, "使用 DropWheel 转换")]
    [InlineData(new[] { "你", "好", "，", "世", "界", "。" }, "你好，世界。")]
    [InlineData(new[] { "2026", "年" }, "2026 年")]
    [InlineData(new[] { "こん", "にちは", "안녕" }, "こんにちは안녕")]
    [InlineData(new[] { " a ", "", "b" }, "a b")]
    public async Task Words_are_spaced_except_between_chinese_japanese_or_korean(string[] words, string line)
    {
        _recognizer.Pages.Enqueue([words]);

        var result = await Run(Image("line.png", 16, 16, MagickFormat.Png));

        Text(result.OutputPaths.Single()).Should().Be(line + "\r\n");
    }

    [Fact]
    public async Task An_image_is_read_upright_on_white_and_no_larger_than_the_recognizer_takes()
    {
        var clear = Image("clear.png", 200, 100, MagickFormat.Png, "none");
        var turned = Path.Combine(_dir, "turned.jpg");
        using (var image = new MagickImage(MagickColors.Blue, 60, 40))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)OrientationType.RightTop);
            image.SetProfile(exif);
            image.Orientation = OrientationType.RightTop;
            image.Write(turned, MagickFormat.Jpeg);
        }

        _recognizer.MaxSide = 50;
        _recognizer.Pages.Enqueue([["a"]]);
        _recognizer.Pages.Enqueue([["b"]]);

        var result = await Run(clear, turned);

        result.Succeeded.Should().BeTrue(result.Error);
        _recognizer.Seen.Should().Equal((50, 25), (33, 50));
        _recognizer.FirstPixels[0].Should().Equal([255, 255, 255, 255], "transparency is put on white and the alpha is opaque");
        _recognizer.FirstPixels[1][3].Should().Be(255);
    }

    [Fact]
    public async Task Every_page_of_a_pdf_is_rendered_and_read_with_a_blank_line_between_pages()
    {
        var pdf = await ThreePagePdf();
        _recognizer.Pages.Enqueue([["one"]]);
        _recognizer.Pages.Enqueue([]);
        _recognizer.Pages.Enqueue([["three"], ["lines"]]);
        var progress = new List<double>();

        var result = await _tool.RunAsync(new([pdf], _tool.Id, ""), new InlineProgress(progress.Add), CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Error);
        Text(result.OutputPaths.Single()).Should().Be("one\r\n\r\nthree\r\nlines\r\n", "a page with no text adds no blank line");
        _recognizer.Seen.Should().Equal((300, 150), (150, 300), (300, 150));
        _recognizer.FirstPixels.Should().AllSatisfy(p => p.Should().Equal([255, 255, 255, 255]), "pages are drawn on white");
        progress.Should().BeInAscendingOrder().And.EndWith(1.0);
    }

    /// <summary>Three pages of 72 × 36, 36 × 72 and 72 × 36 pt: 300 × 150 px and so on at 300 DPI.</summary>
    private async Task<string> ThreePagePdf()
    {
        var pages = new[] { Image("p1.png", 96, 48, MagickFormat.Png, "white"), Image("p2.png", 48, 96, MagickFormat.Png, "white"), Image("p3.png", 96, 48, MagickFormat.Png, "white") };
        var combined = await new CombinePdfTool().RunAsync(new(pages, "tool.combine-pdf", ""), new InlineProgress(_ => { }), CancellationToken.None);
        foreach (var page in pages)
        {
            File.Delete(page);
        }

        return combined.OutputPaths.Single();
    }

    [Fact]
    public async Task With_no_text_anywhere_the_job_is_skipped_and_nothing_is_written()
    {
        var png = Image("blank.png", 32, 32, MagickFormat.Png, "white");
        _recognizer.Pages.Enqueue([[" "]]);

        var result = await Run(png);

        result.Succeeded.Should().BeTrue();
        result.Skipped.Should().BeTrue();
        result.Detail.Should().Be("没有认出文字");
        Directory.GetFiles(_dir).Should().Equal(png);
    }

    [Fact]
    public async Task An_image_without_text_next_to_one_with_text_is_counted()
    {
        var a = Image("a.png", 32, 32, MagickFormat.Png);
        var b = Image("b.png", 32, 32, MagickFormat.Png);
        _recognizer.Pages.Enqueue([["text"]]);
        _recognizer.Pages.Enqueue([]);

        var result = await Run(a, b);

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "a.txt"));
        result.Detail.Should().Be("认出 1 行，识别语言 测试语言；另有 1 个没有认出文字");
    }

    [Fact]
    public void Without_an_ocr_language_it_is_off_with_the_reason()
    {
        _recognizer.Unavailable = "没有识别语言";

        var availability = _tool.Check(new([Image("a.png", 8, 8, MagickFormat.Png)], _tool.Id, ""));

        availability.IsAvailable.Should().BeFalse();
        availability.Reason.Should().Be("没有识别语言");
    }

    [Fact]
    public void It_takes_images_and_pdfs_it_can_read()
    {
        _tool.AppliesTo(MediaKind.Image).Should().BeTrue();
        _tool.AppliesTo(MediaKind.Pdf).Should().BeTrue();
        _tool.AppliesTo(MediaKind.Video).Should().BeFalse();
        _tool.Check(new([Image("a.heic", 8, 8, MagickFormat.Png)], _tool.Id, "")).IsAvailable.Should().BeTrue();
        _tool.Check(new([Path.Combine(_dir, "gone.png")], _tool.Id, "")).Reason.Should().Be("找不到 gone.png");
    }

    [Fact]
    public async Task A_broken_image_an_svg_pointing_outside_and_an_encrypted_pdf_are_named_and_nothing_is_written()
    {
        var bad = Path.Combine(_dir, "bad.png");
        File.WriteAllBytes(bad, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]);
        var trap = Path.Combine(_dir, "trap.svg");
        await File.WriteAllTextAsync(trap, """<svg xmlns="http://www.w3.org/2000/svg" width="9" height="9"><image href="https://example.com/a.png" width="9" height="9"/></svg>""");
        var locked = Path.Combine(_dir, "locked.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "encrypted.pdf"), locked);

        (await Run(bad)).Error.Should().StartWith("bad.png 无法读取：");
        (await Run(trap)).Error.Should().Be("trap.svg 无法识别：SVG 引用了它以外的内容（https://example.com/a.png），为了安全不转换");
        (await Run(locked)).Error.Should().Be("locked.pdf 有密码保护，暂不支持加密的 PDF");
        _recognizer.Seen.Should().BeEmpty();
        Directory.GetFiles(_dir).Should().HaveCount(3);
    }

    private sealed class FakeRecognizer : ITextRecognizer
    {
        public Queue<IReadOnlyList<IReadOnlyList<string>>> Pages { get; } = new();

        public List<(int Width, int Height)> Seen { get; } = [];

        public List<byte[]> FirstPixels { get; } = [];

        public string? Unavailable { get; set; }

        public int MaxSide { get; set; } = 10000;

        public string Language => "测试语言";

        public Task<IReadOnlyList<IReadOnlyList<string>>> ReadAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken)
        {
            bgra.Length.Should().Be(width * height * 4);
            Seen.Add((width, height));
            FirstPixels.Add(bgra[..4]);
            return Task.FromResult(Pages.Count > 0 ? Pages.Dequeue() : []);
        }
    }
}
