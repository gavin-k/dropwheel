using System.Globalization;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Localization;
using DropWheel.Core.Settings;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-settings-").FullName;

    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_file_gives_defaults_and_writes_nothing()
    {
        var store = new SettingsStore(File_);

        store.Current.Should().Be(AppSettings.Default);
        store.LoadProblem.Should().BeNull();
        File.Exists(File_).Should().BeFalse();
    }

    [Fact]
    public void Changes_are_saved_and_read_back()
    {
        var store = new SettingsStore(File_);
        AppSettings? seen = null;
        store.Changed += s => seen = s;

        store.Update(s => s with
        {
            Output = new(OutputLocation.Folder, @"D:\out", "{name}-{date}", ConflictPolicy.Skip),
            Keys = s.Keys with { Style = InvokeStyle.Hold, TapWindowMs = 250 },
            Actions = new([FileAction.CopyPath, FileAction.Rename]),
            Privacy = new(StrictOffline: true),
            OnboardingDone = true,
        });

        seen.Should().BeSameAs(store.Current);
        var reloaded = new SettingsStore(File_);
        reloaded.LoadProblem.Should().BeNull();
        var again = reloaded.Current;
        again.Output.Should().Be(new OutputSettings(OutputLocation.Folder, @"D:\out", "{name}-{date}", ConflictPolicy.Skip));
        again.Keys.Style.Should().Be(InvokeStyle.Hold);
        again.Keys.TapWindowMs.Should().Be(250);
        again.Keys.Bindings.Should().Equal(KeyBindings.Default.Bindings);
        again.Actions.Enabled.Should().BeEquivalentTo([FileAction.CopyPath, FileAction.Rename]);
        again.Privacy.StrictOffline.Should().BeTrue();
        again.OnboardingDone.Should().BeTrue();
    }

    [Fact]
    public void Enums_are_written_as_names()
    {
        new SettingsStore(File_).Update(s => s with { OnboardingDone = true });

        var json = File.ReadAllText(File_);
        json.Should().Contain("\"style\": \"Tap\"").And.Contain("\"mode\": \"Convert\"").And.Contain("\"conflict\": \"AutoNumber\"");
        Directory.GetFiles(_dir).Should().ContainSingle("the temp file is renamed over the target");
    }

    [Fact]
    public void Unreadable_file_is_moved_aside_and_defaults_are_used()
    {
        File.WriteAllText(File_, "{ \"version\": 1, \"output\": ");

        var store = new SettingsStore(File_);

        store.Current.Should().Be(AppSettings.Default);
        store.LoadProblem.Should().Contain("settings.json.bad");
        File.Exists(File_ + ".bad").Should().BeTrue();
        File.Exists(File_).Should().BeFalse();
    }

    [Fact]
    public void Switched_on_tools_round_trip_and_a_missing_section_is_empty()
    {
        var store = new SettingsStore(File_);

        store.Update(s => s with { Tools = s.Tools.Trust("user.strip", "abc123").Trust("user.other", "def") });

        var reloaded = new SettingsStore(File_);
        reloaded.Current.Tools.Should().Be(UserToolSettings.Default.Trust("user.other", "def").Trust("user.strip", "abc123"));
        reloaded.Current.Tools.IsTrusted("user.strip", "abc123").Should().BeTrue();
        reloaded.Current.Tools.IsTrusted("user.strip", "changed").Should().BeFalse();
        File.ReadAllText(File_).Should().Contain("\"user.strip\": \"abc123\"");

        File.WriteAllText(File_, """{ "version": 2, "tools": null }""");
        new SettingsStore(File_).Current.Tools.Should().Be(UserToolSettings.Default);
        File.WriteAllText(File_, """{ "version": 2, "tools": {} }""");
        new SettingsStore(File_).Current.Tools.Should().Be(UserToolSettings.Default);
    }

    [Fact]
    public void Missing_or_null_sections_fall_back_to_defaults()
    {
        File.WriteAllText(File_, """
            // hand-edited
            { "version": 1, "keys": null, "actions": {}, "onboardingDone": true, "somethingNew": 3, }
            """);

        var store = new SettingsStore(File_);
        var s = store.Current;

        store.LoadProblem.Should().BeNull();
        s.Keys.Should().Be(KeyBindings.Default);
        s.Actions.Should().Be(FileActionSettings.Default);
        s.Output.Should().Be(new OutputSettings());
        s.OnboardingDone.Should().BeTrue();
    }

    [Fact]
    public void A_version_1_file_gets_the_new_file_action_default_unless_its_set_was_edited()
    {
        File.WriteAllText(File_, """{ "version": 1, "actions": { "enabled": ["CopyPath", "MoveTo"] } }""");
        var migrated = new SettingsStore(File_).Current;

        File.WriteAllText(File_, """{ "version": 1, "actions": { "enabled": ["CopyPath"] } }""");
        var edited = new SettingsStore(File_).Current;

        File.WriteAllText(File_, """{ "version": 2, "actions": { "enabled": ["CopyPath", "MoveTo"] } }""");
        var current = new SettingsStore(File_).Current;

        migrated.Actions.Should().Be(FileActionSettings.Default);
        migrated.Version.Should().Be(AppSettings.CurrentVersion);
        edited.Actions.Enabled.Should().BeEquivalentTo([FileAction.CopyPath]);
        current.Actions.Enabled.Should().BeEquivalentTo([FileAction.CopyPath, FileAction.MoveTo]);
    }

    [Theory]
    [InlineData(LanguageSetting.System, "zh-CN", UiLanguage.Chinese)]
    [InlineData(LanguageSetting.System, "en-US", UiLanguage.English)]
    [InlineData(LanguageSetting.English, "zh-CN", UiLanguage.English)]
    [InlineData(LanguageSetting.Chinese, "en-US", UiLanguage.Chinese)]
    public void The_language_follows_Windows_unless_one_was_picked(LanguageSetting setting, string display, UiLanguage expected) =>
        (AppSettings.Default with { Language = setting }).LanguageFor(CultureInfo.GetCultureInfo(display)).Should().Be(expected);

    [Fact]
    public void The_picked_language_is_saved_by_name_and_a_file_without_one_follows_Windows()
    {
        new SettingsStore(File_).Update(s => s with { Language = LanguageSetting.English });

        File.ReadAllText(File_).Should().Contain("\"language\": \"English\"");
        new SettingsStore(File_).Current.Language.Should().Be(LanguageSetting.English);
        AppSettings.Default.Language.Should().Be(LanguageSetting.System);
    }

    [Fact]
    public void Request_carries_the_output_settings()
    {
        var settings = AppSettings.Default with { Output = new(OutputLocation.Folder, @"D:\out", "{name}_{n}", ConflictPolicy.Skip) };

        var r = settings.Request([@"C:\a.jpg"], "png");

        r.OutputDirectory.Should().Be(@"D:\out");
        r.Naming.Should().Be("{name}_{n}");
        r.Conflict.Should().Be(ConflictPolicy.Skip);
        r.TargetFormatId.Should().Be("png");
        AppSettings.Default.Request([@"C:\a.jpg"], "png").OutputDirectory.Should().BeEmpty("the default is next to the source");
    }

    [Fact]
    public void Recent_items_survive_a_restart()
    {
        var path = Path.Combine(_dir, "recent.json");
        var recent = new RecentItems(path: path);
        recent.Touch(MediaKind.Image, WheelMode.Convert, "png");
        recent.Touch(MediaKind.Image, WheelMode.Convert, "webp");
        recent.Touch(MediaKind.Video, WheelMode.Tools, "trim");

        var again = new RecentItems(path: path);

        again.For(MediaKind.Image, WheelMode.Convert).Should().Equal("webp", "png");
        again.For(MediaKind.Video, WheelMode.Tools).Should().Equal("trim");
        again.LoadProblem.Should().BeNull();
    }

    [Fact]
    public void Unreadable_recent_file_starts_empty()
    {
        var path = Path.Combine(_dir, "recent.json");
        File.WriteAllText(path, "[1,2");

        var recent = new RecentItems(path: path);

        recent.For(MediaKind.Image, WheelMode.Convert).Should().BeEmpty();
        recent.LoadProblem.Should().NotBeNull();
    }
}
