using System.Security.AccessControl;
using System.Security.Principal;
using DropWheel.Core.Conversion;
using DropWheel.Core.Wheel;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace DropWheel.Core.Tests.Wheel;

public sealed class LayoutStoreFailureTests : IDisposable
{
    private const string InitialLayout = """{ "version": 1, "wheels": { "tools": { "image": [ { "item": "tool.compress-image" } ] } }, "installed": [] }""";
    private const string EditedLayout = """{ "version": 1, "wheels": { "tools": { "image": [ { "item": "tool.ocr" } ] } }, "installed": [] }""";
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-layout-failure-").FullName;

    private string File_ => Path.Combine(_dir, "layout.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_reload_denied_read_access_keeps_the_last_layout_and_recovers_without_another_edit()
    {
        File.WriteAllText(File_, InitialLayout);
        var clock = new FakeTimeProvider();
        using var store = new LayoutStore(File_, clock);
        var previous = store.Current;
        var originalStamp = File.GetLastWriteTimeUtc(File_);
        var changes = new List<WheelLayout>();
        store.Changed += changes.Add;
        File.WriteAllText(File_, EditedLayout);
        File.SetLastWriteTimeUtc(File_, originalStamp.AddSeconds(2));
        var editedStamp = File.GetLastWriteTimeUtc(File_);
        store.Watch();

        Action restoreReadAccess;
        if (OperatingSystem.IsWindows())
        {
            // Re-applying an unmodified FileSecurity persists nothing, so the deny rule has to be removed explicitly.
            var file = new FileInfo(File_);
            var permissions = file.GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            var deny = new FileSystemAccessRule(identity.User!, FileSystemRights.ReadData, AccessControlType.Deny);
            permissions.AddAccessRule(deny);
            file.SetAccessControl(permissions);
            restoreReadAccess = () =>
            {
                if (OperatingSystem.IsWindows())
                {
                    permissions.RemoveAccessRule(deny);
                    file.SetAccessControl(permissions);
                }
            };
        }
        else
        {
            var originalPermissions = File.GetUnixFileMode(File_);
            File.SetUnixFileMode(File_, UnixFileMode.UserWrite);
            restoreReadAccess = () =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(File_, originalPermissions);
                }
            };
        }

        try
        {
            Action read = () =>
            {
                using var stream = File.OpenRead(File_);
            };
            read.Should().Throw<UnauthorizedAccessException>("the test must actually deny reads, not bypass permissions");

            store.Reload().Should().BeFalse();

            store.Current.Should().BeSameAs(previous);
            store.LoadProblem.Should().NotBeNullOrEmpty();
            changes.Should().BeEmpty();
            File.GetLastWriteTimeUtc(File_).Should().Be(editedStamp);
        }
        finally
        {
            restoreReadAccess();
        }

        File.ReadAllText(File_).Should().Be(EditedLayout);
        File.GetLastWriteTimeUtc(File_).Should().Be(editedStamp);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        store.SlotsFor(WheelMode.Tools, MediaKind.Image)!.Single()!.Item.Should().Be("tool.ocr");
        store.LoadProblem.Should().BeNull();
        changes.Should().ContainSingle().Which.Should().BeSameAs(store.Current);
        store.Reload().Should().BeFalse();
        changes.Should().HaveCount(1);
        File.ReadAllText(File_).Should().Be(EditedLayout);
        File.GetLastWriteTimeUtc(File_).Should().Be(editedStamp);
    }

    [Fact]
    public void A_no_op_update_notifies_an_external_reload_once_without_rewriting_the_file()
    {
        File.WriteAllText(File_, InitialLayout);
        using var store = new LayoutStore(File_);
        var originalStamp = File.GetLastWriteTimeUtc(File_);
        var changes = new List<WheelLayout>();
        store.Changed += changes.Add;
        File.WriteAllText(File_, EditedLayout);
        File.SetLastWriteTimeUtc(File_, originalStamp.AddSeconds(2));
        var editedStamp = File.GetLastWriteTimeUtc(File_);
        WheelLayout? suppliedToUpdate = null;

        var result = store.Update(layout =>
        {
            suppliedToUpdate = layout;
            return layout;
        });

        result.Should().BeSameAs(store.Current).And.BeSameAs(suppliedToUpdate);
        result.SlotsFor(WheelMode.Tools, MediaKind.Image)!.Single()!.Item.Should().Be("tool.ocr");
        changes.Should().ContainSingle().Which.Should().BeSameAs(result);
        File.ReadAllText(File_).Should().Be(EditedLayout);
        File.GetLastWriteTimeUtc(File_).Should().Be(editedStamp);

        store.Update(layout => layout).Should().BeSameAs(result);
        store.Reload().Should().BeFalse();
        changes.Should().HaveCount(1);
        File.ReadAllText(File_).Should().Be(EditedLayout);
        File.GetLastWriteTimeUtc(File_).Should().Be(editedStamp);
    }
}
