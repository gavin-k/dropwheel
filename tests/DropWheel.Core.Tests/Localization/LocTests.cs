using System.Globalization;
using DropWheel.Core.Localization;
using FluentAssertions;

namespace DropWheel.Core.Tests.Localization;

public sealed class LocTests
{
    private static readonly Dictionary<string, string> English = new()
    {
        ["{0} 个文件"] = "{0} {0|file|files}",
        ["{0} · {1} 项"] = "{0} · {1} {1|item|items}",
    };

    [Fact]
    public void WithoutACatalogTheChineseIsUsed() =>
        Loc.Text("{0} 个文件", null, 3).Should().Be("3 个文件");

    [Fact]
    public void TextTheCatalogLacksStaysChinese() =>
        Loc.Text("已复制路径", English).Should().Be("已复制路径");

    [Theory]
    [InlineData(1, "1 file")]
    [InlineData(2, "2 files")]
    [InlineData(0, "0 files")]
    public void EnglishPicksTheWordForTheCount(int count, string expected) =>
        Loc.Text("{0} 个文件", English, count).Should().Be(expected);

    [Fact]
    public void ThePluralFollowsItsOwnArgument() =>
        Loc.Text("{0} · {1} 项", English, "Tools", 1).Should().Be("Tools · 1 item");

    [Theory]
    [InlineData("zh-CN", UiLanguage.Chinese)]
    [InlineData("zh-TW", UiLanguage.Chinese)]
    [InlineData("zh-Hans", UiLanguage.Chinese)]
    [InlineData("en-US", UiLanguage.English)]
    [InlineData("ja-JP", UiLanguage.English)]
    [InlineData("de-DE", UiLanguage.English)]
    public void ChineseWindowsGetsChineseAndTheRestEnglish(string culture, UiLanguage expected) =>
        Loc.For(CultureInfo.GetCultureInfo(culture)).Should().Be(expected);

    [Fact]
    public void TheLibrariesSpeakChineseUntilTheAppSaysOtherwise() =>
        Loc.Current.Should().Be(UiLanguage.Chinese);

    [Fact]
    public void AnAssemblyWithoutACatalogHasAnEmptyOne() =>
        Loc.Catalog(typeof(LocTests).Assembly).Should().BeEmpty();
}
