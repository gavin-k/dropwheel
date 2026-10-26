using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class DefaultRecommenderTests
{
    private static ConversionGraph Graph(params string[] formats)
    {
        var graph = new ConversionGraph();
        graph.Register(new TestConverter(TestConverter.Between(formats)));
        return graph;
    }

    [Fact]
    public void Jpg_gets_the_image_preferences_without_itself()
    {
        var graph = Graph("jpg", "png", "webp", "avif", "gif", "bmp", "tiff");
        new DefaultRecommender(graph).For(["a.jpg"], MediaKind.Image, WheelMode.Convert)
            .Should().Equal("png", "webp", "avif", "gif", "tiff", "bmp");
    }

    [Fact]
    public void Png_is_offered_jpg_first()
    {
        var graph = Graph("jpg", "png", "webp");
        new DefaultRecommender(graph).For(["a.png"], MediaKind.Image, WheelMode.Convert)
            .Should().Equal("jpg", "webp");
    }

    [Fact]
    public void Images_offer_pdf_after_the_two_common_formats()
    {
        var graph = Graph("jpg", "png", "webp", "avif", "gif", "bmp", "tiff", "pdf");
        new DefaultRecommender(graph).For(["a.png"], MediaKind.Image, WheelMode.Convert).Take(3)
            .Should().Equal("jpg", "pdf", "webp");
    }

    [Fact]
    public void A_pdf_is_offered_as_docx_then_page_images_then_text()
    {
        var graph = new ConversionGraph();
        graph.Register(new TestConverter([new("pdf", "txt"), new("pdf", "jpg"), new("pdf", "png"), new("pdf", "docx")]));
        new DefaultRecommender(graph).For(["a.pdf"], MediaKind.Pdf, WheelMode.Convert)
            .Should().Equal("docx", "png", "jpg", "txt");
    }

    [Fact]
    public void A_word_document_is_offered_as_text_then_markdown_then_html()
    {
        var graph = new ConversionGraph();
        graph.Register(new TestConverter([new("docx", "html"), new("docx", "md"), new("docx", "txt"), new("txt", "docx")]));
        var recommender = new DefaultRecommender(graph);
        recommender.For(["a.docx"], MediaKind.Document, WheelMode.Convert).Should().Equal("txt", "md", "html");
        recommender.For(["a.txt"], MediaKind.Document, WheelMode.Convert).Should().Equal("docx");
    }

    [Fact]
    public void A_spreadsheet_is_offered_as_xlsx_then_csv_then_tsv()
    {
        var graph = new ConversionGraph();
        graph.Register(new TestConverter([new("xlsx", "tsv"), new("xlsx", "csv"), new("csv", "xlsx"), new("csv", "tsv")]));
        var recommender = new DefaultRecommender(graph);
        recommender.For(["a.xlsx"], MediaKind.Sheet, WheelMode.Convert).Should().Equal("csv", "tsv");
        recommender.For(["a.csv"], MediaKind.Sheet, WheelMode.Convert).Should().Equal("xlsx", "tsv");
    }

    [Fact]
    public void Video_offers_other_containers_then_audio_and_gif()
    {
        var graph = Graph("mp4", "mkv", "mov", "webm", "avi", "mp3", "m4a", "wav", "gif");
        new DefaultRecommender(graph).For(["a.mp4"], MediaKind.Video, WheelMode.Convert).Take(6)
            .Should().Equal("mkv", "mov", "webm", "mp3", "gif", "m4a");
    }

    [Fact]
    public void Audio_offers_mp3_first()
    {
        var graph = Graph("mp3", "m4a", "wav", "flac", "aac", "ogg", "opus", "aiff");
        new DefaultRecommender(graph).For(["a.flac"], MediaKind.Audio, WheelMode.Convert).Take(6)
            .Should().Equal("mp3", "m4a", "wav", "aac", "ogg", "opus");
    }

    [Fact]
    public void Unreachable_targets_are_dropped_and_extra_ones_follow()
    {
        var graph = new ConversionGraph();
        graph.Register(new TestConverter([new("jpg", "png"), new("jpg", "heic")]));
        new DefaultRecommender(graph).For(["a.jpg"], MediaKind.Image, WheelMode.Convert)
            .Should().Equal("png", "heic");
    }

    [Theory]
    [InlineData(WheelMode.Tools)]
    [InlineData(WheelMode.Actions)]
    public void Only_the_convert_wheel_has_recommendations_in_M0(WheelMode mode)
        => new DefaultRecommender(Graph("jpg", "png")).For(["a.jpg"], MediaKind.Image, mode).Should().BeEmpty();

    [Fact]
    public void Wheel_puts_recent_first_and_fills_with_recommendations()
    {
        var graph = Graph("jpg", "png", "webp", "avif", "gif", "bmp", "tiff");
        var recent = new RecentItems();
        recent.Touch(MediaKind.Image, WheelMode.Convert, "webp");

        var wheel = new WheelBuilder(graph, recent, new DefaultRecommender(graph)).Build(["/p/a.jpg"], WheelMode.Convert, AppSettings.Default);

        wheel.Slots.Select(s => s.ItemId).Should().Equal("webp", "png", "avif", "gif", "tiff", "bmp");
        wheel.Slots[0].SubLabel.Should().Be("最近");
        wheel.Slots[1].SubLabel.Should().Be("推荐");
        wheel.CenterTitle.Should().Be("a.jpg");
    }
}
