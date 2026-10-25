// PDFtoImage marks its API with the platforms it ships PDFium for; these tests run on all of them.
#pragma warning disable CA1416

using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;
using DropWheel.Engines.Tools;
using FluentAssertions;
using ImageMagick;
using PDFtoImage;
using SkiaSharp;

namespace DropWheel.Engines.Tests;

public sealed class PdfToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-pdftools-").FullName;
    private readonly MergePdfTool _merge = new();
    private readonly SplitPdfTool _split = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>One page per size, each with a grey box on it.</summary>
    private string Pdf(string name, params (float Width, float Height)[] pages)
    {
        var path = Path.Combine(_dir, name);
        using var output = File.Create(path);
        using var document = SKDocument.CreatePdf(output);
        using var paint = new SKPaint { Color = SKColors.Gray };
        foreach (var (width, height) in pages)
        {
            document.BeginPage(width, height).DrawRect(10, 10, width / 2, height / 2, paint);
            document.EndPage();
        }

        document.Close();
        return path;
    }

    private string Locked(string name)
    {
        var path = Path.Combine(_dir, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "encrypted.pdf"), path);
        return path;
    }

    private static Task<ConversionResult> Run(IWheelOperation tool, string[] sources, List<double>? progress = null) =>
        tool.RunAsync(new(sources, tool.Id, ""), new InlineProgress(p => progress?.Add(p)), CancellationToken.None);

    private static IList<System.Drawing.SizeF> PageSizes(string pdf)
    {
        using var input = File.OpenRead(pdf);
        return Conversion.GetPageSizes(input);
    }

    private static SKBitmap Render(string pdf)
    {
        using var input = File.OpenRead(pdf);
        return Conversion.ToImage(input, leaveOpen: false, page: 0, options: new(Dpi: 144));
    }

    [Fact]
    public async Task Pdfs_are_merged_in_file_name_order()
    {
        var ten = Pdf("Part 10.pdf", (100, 50));
        var two = Pdf("part 2.pdf", (200, 100), (300, 150));
        var progress = new List<double>();

        var result = await Run(_merge, [ten, two], progress);

        result.Succeeded.Should().BeTrue(result.Error);
        var merged = Path.Combine(_dir, "part 2-合并.pdf");
        result.OutputPaths.Should().Equal(merged);
        result.Detail.Should().Be("2 个文件，共 3 页");
        PageSizes(merged).Should().Equal(new System.Drawing.SizeF(200, 100), new System.Drawing.SizeF(300, 150), new System.Drawing.SizeF(100, 50));
        progress.Should().BeInAscendingOrder().And.EndWith(1.0);
        Directory.GetFiles(_dir).Should().BeEquivalentTo([ten, two, merged], "no temporary file is left behind");
    }

    [Fact]
    public async Task A_page_with_a_photo_and_text_looks_the_same_after_merging()
    {
        byte[] jpeg;
        using (var image = new MagickImage("gradient:red-blue", 120, 80))
        {
            jpeg = image.ToByteArray(MagickFormat.Jpeg);
        }

        var source = Path.Combine(_dir, "photo.pdf");
        using (var output = File.Create(source))
        using (var document = SKDocument.CreatePdf(output))
        using (var photo = SKImage.FromEncodedData(jpeg))
        using (var font = new SKFont(SKTypeface.Default, 18))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            var canvas = document.BeginPage(300, 200);
            canvas.DrawImage(photo, new SKRect(20, 20, 140, 100), new SKSamplingOptions(SKFilterMode.Linear));
            canvas.DrawText("Quarterly report 2026", 20, 150, SKTextAlign.Left, font, paint);
            document.EndPage();
            document.Close();
        }

        var result = await Run(_merge, [source, Pdf("second.pdf", (100, 100))]);

        result.Succeeded.Should().BeTrue(result.Error);
        using var before = Render(source);
        using var after = Render(result.OutputPaths[0]);
        (after.Width, after.Height).Should().Be((before.Width, before.Height));
        var a = before.GetPixelSpan();
        var b = after.GetPixelSpan();
        int different = 0, dark = 0;
        for (var i = 0; i < a.Length; i++)
        {
            different += a[i] == b[i] ? 0 : 1;
            dark += a[i] < 128 ? 1 : 0;
        }

        dark.Should().BeGreaterThan(1000, "the photo is on the page");
        different.Should().Be(0, "the page is copied, not redrawn");
    }

    [Fact]
    public void Merging_needs_two_pdfs()
    {
        var one = Pdf("a.pdf", (100, 100));

        var availability = _merge.Check(new([one], _merge.Id, ""));

        availability.IsAvailable.Should().BeFalse();
        availability.Cause.Should().Be(UnavailableCause.SelectionShape);
        _merge.Check(new([one, Pdf("b.pdf", (100, 100))], _merge.Id, "")).IsAvailable.Should().BeTrue();
        _merge.Check(new([one, Path.Combine(_dir, "notes.txt")], _merge.Id, "")).Reason.Should().Be("notes.txt 不是 PDF");
        _merge.Check(new([one, Path.Combine(_dir, "gone.pdf")], _merge.Id, "")).Reason.Should().Be("找不到 gone.pdf");
    }

    [Fact]
    public async Task A_password_protected_pdf_is_not_merged()
    {
        var open = Pdf("a.pdf", (100, 100));
        var locked = Locked("b.pdf");

        var result = await Run(_merge, [open, locked]);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("b.pdf 有密码保护，暂不支持加密的 PDF");
        Directory.GetFileSystemEntries(_dir).Should().BeEquivalentTo([open, locked]);
    }

    [Fact]
    public async Task A_broken_pdf_is_named_and_nothing_is_merged()
    {
        var good = Pdf("a.pdf", (100, 100));
        var broken = Path.Combine(_dir, "broken.pdf");
        File.WriteAllText(broken, "%PDF-1.7\nnot really a pdf\n");

        var result = await Run(_merge, [good, broken]);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().StartWith("broken.pdf 无法读取：");
        Directory.GetFileSystemEntries(_dir).Should().BeEquivalentTo([good, broken]);
    }

    [Fact]
    public async Task Each_page_becomes_a_pdf_in_a_folder()
    {
        var report = Pdf("report.pdf", (200, 100), (300, 150), (100, 50));
        var progress = new List<double>();

        var result = await Run(_split, [report], progress);

        result.Succeeded.Should().BeTrue(result.Error);
        var folder = Path.Combine(_dir, "report");
        result.OutputPaths.Should().Equal(folder);
        result.Detail.Should().Be("共 3 页");
        Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).Should().Equal("report-01.pdf", "report-02.pdf", "report-03.pdf");
        PageSizes(Path.Combine(folder, "report-02.pdf")).Should().Equal(new System.Drawing.SizeF(300, 150));
        progress.Should().BeInAscendingOrder().And.EndWith(1.0);
        Directory.GetFileSystemEntries(_dir).Should().BeEquivalentTo([report, folder], "no temporary folder is left behind");
    }

    [Fact]
    public async Task A_one_page_pdf_is_left_as_it_is()
    {
        var memo = Pdf("memo.pdf", (100, 100));

        var result = await Run(_split, [memo]);

        result.Succeeded.Should().BeTrue();
        result.Skipped.Should().BeTrue();
        result.Detail.Should().Be("只有一页，不用拆分");
        Directory.GetFileSystemEntries(_dir).Should().Equal(memo);
    }

    [Fact]
    public async Task One_page_pdfs_in_a_batch_are_counted()
    {
        var report = Pdf("report.pdf", (200, 100), (300, 150));
        var memo = Pdf("memo.pdf", (100, 100));

        var result = await Run(_split, [report, memo]);

        result.Succeeded.Should().BeTrue(result.Error);
        result.OutputPaths.Should().Equal(Path.Combine(_dir, "report"));
        result.Detail.Should().Be("共 2 页；另有 1 个只有一页，不用拆分");
    }

    [Fact]
    public async Task A_password_protected_pdf_fails_the_split_and_removes_what_it_wrote()
    {
        var report = Pdf("report.pdf", (200, 100), (300, 150));
        var locked = Locked("secret.pdf");

        var result = await Run(_split, [report, locked]);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("secret.pdf 有密码保护，暂不支持加密的 PDF");
        Directory.GetFileSystemEntries(_dir).Should().BeEquivalentTo([report, locked]);
    }
}
