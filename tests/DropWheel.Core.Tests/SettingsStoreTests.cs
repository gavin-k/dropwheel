using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
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
