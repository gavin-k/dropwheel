using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DropWheel.Core.Localization;

/// <summary>The language DropWheel speaks to the user in.</summary>
public enum UiLanguage { Chinese, English }

/// <summary>
/// User-facing text is written in Chinese where it is used, through the assembly's own <c>L.T</c>, and the Chinese is
/// also the key into the English catalog the assembly embeds as Strings.en.json. <see cref="Current"/> stays Chinese
/// until the app sets it, once, at startup; the libraries and their tests speak Chinese unless told otherwise.
/// </summary>
public static partial class Loc
{
    public const string CatalogName = "Strings.en.json";

    public static UiLanguage Current { get; set; } = UiLanguage.Chinese;

    /// <summary>The language for a Windows display language: Chinese for any Chinese, English for the rest.</summary>
    public static UiLanguage For(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "zh" ? UiLanguage.Chinese : UiLanguage.English;

    /// <summary>The English catalog <paramref name="assembly"/> embeds, Chinese to English; empty when it has none.</summary>
    public static IReadOnlyDictionary<string, string> Catalog(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(CatalogName);
        if (stream is null)
        {
            return new Dictionary<string, string>();
        }

        using var json = JsonDocument.Parse(stream);
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }

    /// <summary>
    /// <paramref name="chinese"/>, or its English in <paramref name="english"/> when there is one, with
    /// <paramref name="args"/> put in as <see cref="string.Format(IFormatProvider, string, object[])"/> does. English
    /// can say <c>{0|file|files}</c> for the first word when argument 0 is 1 and the second otherwise.
    /// </summary>
    public static string Text(string chinese, IReadOnlyDictionary<string, string>? english, params object?[] args)
    {
        var template = chinese;
        if (english is not null && english.TryGetValue(chinese, out var translated))
        {
            template = Plural().Replace(translated, m =>
                args[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] is { } n && Convert.ToDouble(n, CultureInfo.InvariantCulture) == 1
                    ? m.Groups[2].Value
                    : m.Groups[3].Value);
        }

        return args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
    }

    [GeneratedRegex(@"\{(\d+)\|([^|{}]*)\|([^|{}]*)\}")]
    private static partial Regex Plural();
}
