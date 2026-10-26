using DropWheel.Core.Localization;
using DropWheel.Engines.Document;
using FluentAssertions;

namespace DropWheel.Engines.Tests;

/// <summary>The engines carry their own English, apart from Core's; the tests otherwise run in Chinese.</summary>
public sealed class EnglishTests
{
    private static readonly IReadOnlyDictionary<string, string> English = Loc.Catalog(typeof(DocumentConverter).Assembly);

    [Fact]
    public void TheEnginesEmbedTheirOwnCatalog() =>
        English.Should().ContainKey("幻灯片 {0}").And.NotContainKey("转换失败。");

    [Fact]
    public void ACountReadsAsEnglish() =>
        Loc.Text("{0} 个文件，共 {1} 页", English, 1, 12).Should().Be("1 file, 12 pages in all");

    [Fact]
    public void AFailureNamesTheFileInEnglish() =>
        Loc.Text("{0} 转换失败（ffmpeg 退出码 {1}）", English, "clip.mov", 1).Should().Be("Couldn't convert clip.mov (ffmpeg exit code 1)");
}
