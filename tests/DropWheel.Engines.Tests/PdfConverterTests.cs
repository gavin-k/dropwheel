// PDFtoImage marks its API with the platforms it ships PDFium for; these tests run on all of them.
#pragma warning disable CA1416

using System.Globalization;
using System.Text;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Engines.Pdf;
using FluentAssertions;
using ImageMagick;
using ImageMagick.Drawing;
using PDFtoImage;
using SkiaSharp;

namespace DropWheel.Engines.Tests;

public sealed class PdfConverterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-pdf-").FullName;
    private readonly PdfConverter _converter = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// One 200 × 100 pt page per text, with a box below it; an empty text gives a page with only the box. Written by
    /// hand in Helvetica, one of the standard fonts every PDF reader has: Skia on the Linux runner finds no font at all.
    /// </summary>
    private string Pdf(string name, params string[] pages)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "" };
        var kids = new List<string>();
        foreach (var text in pages)
        {
            var content = $"0 g 10 10 50 30 re f BT /F1 18 Tf 10 60 Td ({text}) Tj ET";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents {objects.Count} 0 R "
                + "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
            kids.Add($"{objects.Count} 0 R");
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {kids.Count} >>";
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var (body, number) in objects.Select((o, i) => (o, i + 1)))
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{number} 0 obj\n{body}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        offsets.ForEach(o => pdf.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n"));
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        return path;
    }

    private Task<ConversionResult> Run(ConversionRequest request, List<double>? progress = null) =>
        _converter.RunAsync(request, new InlineProgress(p => progress?.Add(p)), CancellationToken.None);

    private static SKBitmap Render(string pdf, int page, int dpi)
    {
        using var input = File.OpenRead(pdf);
        return Conversion.ToImage(input, leaveOpen: false, page: page, options: new(Dpi: dpi));
    }

    private static IList<System.Drawing.SizeF> PageSizes(string pdf)
    {
        using var input = File.OpenRead(pdf);
        return Conversion.GetPageSizes(input);
    }

    [Fact]
    public void Edges_cover_pdf_to_pages_and_text_and_images_to_pdf()
    {
        string[] outputs = ["png", "jpg", "txt"];
        string[] inputs = ["jpg", "png", "webp", "avif", "gif", "bmp", "tiff"];
        _converter.Edges.Should().BeEquivalentTo(
            outputs.Select(o => new ConversionEdge("pdf", o)).Concat(inputs.Select(i => new ConversionEdge(i, "pdf"))));
    }

    [Theory]
    [InlineData("png", MagickFormat.Png)]
    [InlineData("jpg", MagickFormat.Jpeg)]
    public async Task A_one_page_pdf_becomes_one_image_next_to_it(string target, MagickFormat format)
    {
        var source = Pdf("letter.pdf", "Hello");

        var result = await Run(new([source], target, ""));

        result.Succeeded.Should().BeTrue(result.Error);
        result.OutputPaths.Should().Equal(Path.Combine(_dir, $"letter.{target}"));
        var info = new MagickImageInfo(result.OutputPaths[0]);
        info.Format.Should().Be(format);
        (info.Width, info.Height).Should().Be((417u, 208u), "200 × 100 pt at 150 DPI, rounded");
        Directory.GetFileSystemEntries(_dir).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_page_is_rendered_on_white_without_alpha()
    {
        var result = await Run(new([Pdf("a.pdf", "")], "png", ""));

        using var page = new MagickImage(result.OutputPaths[0]);
        page.HasAlpha.Should().BeFalse();
        page.GetPixels().GetPixel(150, 100).ToColor()!.ToString().Should().Be(MagickColors.White.ToString());
        page.GetPixels().GetPixel(40, 150).ToColor()!.ToString().Should().Be(MagickColors.Black.ToString(), "the box is drawn");
    }

    [Fact]
    public async Task Several_pages_go_into_a_folder_named_after_the_pdf()
    {
        var source = Pdf("report.pdf", "one", "two", "three");
        var progress = new List<double>();

        var result = await Run(new([source], "png", ""), progress);

        result.Succeeded.Should().BeTrue(result.Error);
        var folder = Path.Combine(_dir, "report");
        result.OutputPaths.Should().Equal(folder);
        Directory.GetFiles(folder).Select(Path.GetFileName).Should().BeEquivalentTo(["report-01.png", "report-02.png", "report-03.png"]);
        progress.Should().BeInAscendingOrder().And.EndWith(1.0).And.HaveCountGreaterThan(3);
        Directory.GetFileSystemEntries(_dir).Should().HaveCount(2, "no temporary folder is left behind");
    }

    [Fact]
    public async Task A_folder_or_file_with_the_name_already_there_is_not_touched()
    {
        var source = Pdf("report.pdf", "one", "two");
        Directory.CreateDirectory(Path.Combine(_dir, "report"));
        File.WriteAllText(Path.Combine(_dir, "report", "keep.txt"), "mine");

        var result = await Run(new([source], "png", ""));

        result.OutputPaths.Should().Equal(Path.Combine(_dir, "report (1)"));
        Directory.GetFiles(Path.Combine(_dir, "report")).Select(Path.GetFileName).Should().Equal("keep.txt");
    }

    [Fact]
    public async Task A_huge_page_is_rendered_at_a_lower_dpi_on_its_own()
    {
        var path = Path.Combine(_dir, "poster.pdf");
        using (var output = File.Create(path))
        using (var document = SKDocument.CreatePdf(output))
        {
            document.BeginPage(7200, 720);
            document.EndPage();
            document.BeginPage(240, 150);
            document.EndPage();
            document.Close();
        }

        var result = await Run(new([path], "png", ""));

        result.Succeeded.Should().BeTrue(result.Error);
        new MagickImageInfo(Path.Combine(_dir, "poster", "poster-01.png")).Width.Should().Be(10000, "100 inches at 100 DPI instead of 150");
        var second = new MagickImageInfo(Path.Combine(_dir, "poster", "poster-02.png"));
        (second.Width, second.Height).Should().Be((500u, 313u), "the other page keeps 150 DPI, and 500 px is not truncated to 499");
    }

    [Fact]
    public async Task Text_comes_out_page_by_page_as_utf8_with_crlf()
    {
        var source = Pdf("notes.pdf", "Page one", "Page two");

        var result = await Run(new([source], "txt", ""));

        result.Succeeded.Should().BeTrue(result.Error);
        var bytes = File.ReadAllBytes(result.OutputPaths.Single());
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Should().Be("Page one\r\n\r\nPage two\r\n");
    }

    [Fact]
    public async Task A_pdf_without_text_fails_instead_of_writing_an_empty_file()
    {
        var source = Pdf("scan.pdf", "", "");

        var result = await Run(new([source], "txt", ""));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("scan.pdf 没有可提取的文字（可能是扫描件，需要 OCR）");
        Directory.GetFileSystemEntries(_dir).Should().Equal(source);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("txt")]
    public async Task A_password_protected_pdf_is_refused(string target)
    {
        var source = Path.Combine(_dir, "locked.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "encrypted.pdf"), source);

        var result = await Run(new([source], target, ""));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("locked.pdf 有密码保护，暂不支持加密的 PDF");
        Directory.GetFileSystemEntries(_dir).Should().Equal(source);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("txt")]
    public async Task A_broken_pdf_fails_the_batch_and_removes_what_it_wrote(string target)
    {
        var good = Pdf("good.pdf", "fine");
        var broken = Path.Combine(_dir, "broken.pdf");
        File.WriteAllText(broken, "%PDF-1.7\nnot really a pdf\n");

        var result = await Run(new([good, broken], target, ""));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().StartWith("broken.pdf 无法读取：");
        Directory.GetFileSystemEntries(_dir).Should().BeEquivalentTo([good, broken]);
    }

    private string Jpeg(string name, OrientationType orientation, double dpi)
    {
        // 40 × 20 stored pixels, the stored top-left quarter red.
        using var image = new MagickImage(MagickColors.White, 40, 20);
        image.Draw(new Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 19, 9));
        image.Density = new Density(dpi, DensityUnit.PixelsPerInch);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)orientation);
        image.SetProfile(exif);
        image.Orientation = orientation;
        var path = Path.Combine(_dir, name);
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    [Fact]
    public async Task A_jpeg_goes_in_as_it_is_and_turned_by_its_exif_orientation()
    {
        var source = Jpeg("photo.jpg", OrientationType.RightTop, 160);

        var result = await Run(new([source], "pdf", ""));

        result.Succeeded.Should().BeTrue(result.Error);
        var pdf = result.OutputPaths.Single();
        pdf.Should().Be(Path.Combine(_dir, "photo.pdf"));
        PageSizes(pdf).Should().Equal([new System.Drawing.SizeF(9, 18)], "20 × 40 upright pixels at 160 DPI");
        var bytes = File.ReadAllBytes(pdf);
        Encoding.Latin1.GetString(bytes).Should().Contain("/DCTDecode", "the JPEG is not encoded again");
        bytes.AsSpan().IndexOf("Exif\0\0"u8).Should().Be(-1, "EXIF, where a GPS position would be, is left out");

        // Turned a quarter clockwise: the stored top-left corner is now the top-right one.
        using var page = Render(pdf, 0, 160);
        (page.Width, page.Height).Should().Be((20, 40));
        page.GetPixel(17, 3).Red.Should().BeGreaterThan(200);
        page.GetPixel(17, 3).Green.Should().BeLessThan(80);
        page.GetPixel(3, 3).Green.Should().BeGreaterThan(200);
        page.GetPixel(17, 30).Green.Should().BeGreaterThan(200);
    }

    [Fact]
    public async Task An_image_without_a_dpi_gets_96()
    {
        var source = Path.Combine(_dir, "shot.png");
        using (var image = new MagickImage(MagickColors.Blue, 96, 48))
        {
            image.Write(source, MagickFormat.Png);
        }

        var result = await Run(new([source], "pdf", ""));

        PageSizes(result.OutputPaths.Single()).Should().Equal(new System.Drawing.SizeF(72, 36));
    }

    [Theory]
    [InlineData("png", MagickFormat.Png, false)]
    [InlineData("webp", MagickFormat.WebP, true)]
    public async Task Lossless_images_stay_lossless_and_opaque_webp_is_stored_as_jpeg(string from, MagickFormat format, bool jpeg)
    {
        var source = Path.Combine(_dir, $"pic.{from}");
        using (var image = new MagickImage("gradient:red-blue", 64, 48))
        {
            image.Write(source, format);
        }

        var result = await Run(new([source], "pdf", ""));

        result.Succeeded.Should().BeTrue(result.Error);
        var pdf = result.OutputPaths.Single();
        PageSizes(pdf).Should().Equal(new System.Drawing.SizeF(48, 36));
        Encoding.Latin1.GetString(File.ReadAllBytes(pdf)).Contains("/DCTDecode", StringComparison.Ordinal).Should().Be(jpeg);
    }

    [Fact]
    public async Task Every_tiff_frame_becomes_a_page_and_a_gif_gives_its_first_frame()
    {
        var tiff = Path.Combine(_dir, "scan.tiff");
        var gif = Path.Combine(_dir, "anim.gif");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 30, 20));
            frames.Add(new MagickImage(MagickColors.Green, 30, 20));
            frames.Add(new MagickImage(MagickColors.Blue, 20, 30));
            frames.Write(tiff, MagickFormat.Tiff);
            frames.Write(gif, MagickFormat.Gif);
        }

        var tiffPdf = (await Run(new([tiff], "pdf", ""))).OutputPaths.Single();
        var gifPdf = (await Run(new([gif], "pdf", ""))).OutputPaths.Single();

        PageSizes(tiffPdf).Should().HaveCount(3);
        PageSizes(tiffPdf)[2].Width.Should().BeLessThan(PageSizes(tiffPdf)[2].Height);
        PageSizes(gifPdf).Should().HaveCount(1);
        using var first = Render(gifPdf, 0, 96);
        first.GetPixel(5, 5).Red.Should().BeGreaterThan(200);
    }

    [Fact]
    public void Exif_is_cut_from_a_jpeg_and_anything_odd_is_left_as_it_is()
    {
        byte[] app0 = [0xFF, 0xE0, 0x00, 0x04, 0x01, 0x02];
        byte[] exif = [0xFF, 0xE1, 0x00, 0x08, .. "Exif\0\0"u8];
        byte[] xmp = [0xFF, 0xE1, 0x00, 0x05, (byte)'h', (byte)'t', (byte)'t'];
        byte[] scan = [0xFF, 0xDA, 0xFF, 0xE1, 0xFF, 0xD9];

        ImagePdf.WithoutExif([0xFF, 0xD8, .. app0, .. exif, .. xmp, .. scan]).Should().Equal([0xFF, 0xD8, .. app0, .. xmp, .. scan]);
        byte[] truncated = [0xFF, 0xD8, .. app0, 0xFF, 0xE1, 0x00, 0x40];
        ImagePdf.WithoutExif(truncated).Should().BeSameAs(truncated);
        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        ImagePdf.WithoutExif(png).Should().BeSameAs(png);
    }

    [Fact]
    public void Estimates_name_what_is_lost()
    {
        QualityEstimate? Estimate(string source, string target) => _converter.Estimate(new([source], target, ""));

        Estimate("a.pdf", "png")!.Level.Should().Be(QualityLevel.High);
        Estimate("a.pdf", "txt")!.Level.Should().Be(QualityLevel.Medium);
        Estimate("a.webp", "pdf")!.Reasons.Should().ContainMatch("*JPG*");
        Estimate("a.png", "pdf").Should().BeNull();
    }
}
