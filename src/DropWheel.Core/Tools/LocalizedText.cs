using DropWheel.Core.Localization;

namespace DropWheel.Core.Tools;

/// <summary>
/// A label a user tool's manifest carries in Chinese and, optionally, English. It is the tool author's data,
/// not DropWheel's own text, so it never goes through the catalog; <see cref="Pick"/> chooses by <see cref="Loc.Current"/>.
/// </summary>
public sealed record LocalizedText(string Zh, string? En = null)
{
    public string Pick() => Pick(Loc.Current);

    public string Pick(UiLanguage language) => language == UiLanguage.English && !string.IsNullOrEmpty(En) ? En : Zh;
}
