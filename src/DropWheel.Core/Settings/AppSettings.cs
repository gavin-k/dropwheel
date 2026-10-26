using System.Globalization;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Localization;

namespace DropWheel.Core.Settings;

public enum OutputLocation { NextToSource, Folder }

/// <summary>The language the user picked; <see cref="System"/> follows the Windows display language.</summary>
public enum LanguageSetting { System, Chinese, English }

/// <summary>Where results are written and what they are called.</summary>
public sealed record OutputSettings(
    OutputLocation Location = OutputLocation.NextToSource,
    string Folder = "",
    string Naming = NamingTemplate.Default,
    ConflictPolicy Conflict = ConflictPolicy.AutoNumber)
{
    /// <summary>The <see cref="ConversionRequest.OutputDirectory"/> for these settings; empty means next to each source.</summary>
    public string Directory => Location == OutputLocation.Folder ? Folder : "";
}

/// <param name="StrictOffline">Refuse every network request, including the update check and optional module downloads.</param>
public sealed record PrivacySettings(bool StrictOffline = false, bool CheckForUpdates = true);

/// <summary>Everything the user can change in Settings. Immutable; <see cref="SettingsStore"/> swaps whole snapshots.</summary>
public sealed record AppSettings
{
    /// <summary>2: the Actions wheel's items got a new default, see <see cref="SettingsStore"/>.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; init; } = CurrentVersion;

    public KeyBindings Keys { get; init; } = KeyBindings.Default;

    public OutputSettings Output { get; init; } = new();

    public FileActionSettings Actions { get; init; } = FileActionSettings.Default;

    public PrivacySettings Privacy { get; init; } = new();

    /// <summary>The first-run guide has been shown (finished or skipped).</summary>
    public bool OnboardingDone { get; init; }

    /// <summary>Takes effect at the next start: the windows are built once, in the language of their start.</summary>
    public LanguageSetting Language { get; init; }

    public static AppSettings Default { get; } = new();

    /// <summary>The language to speak when Windows shows <paramref name="display"/>.</summary>
    public UiLanguage LanguageFor(CultureInfo display) => Language switch
    {
        LanguageSetting.Chinese => UiLanguage.Chinese,
        LanguageSetting.English => UiLanguage.English,
        _ => Loc.For(display),
    };

    /// <summary>A conversion of <paramref name="sourcePaths"/> to <paramref name="targetFormatId"/> under these settings.</summary>
    public ConversionRequest Request(IReadOnlyList<string> sourcePaths, string targetFormatId) =>
        new(sourcePaths, targetFormatId, Output.Directory) { Naming = Output.Naming, Conflict = Output.Conflict };
}
