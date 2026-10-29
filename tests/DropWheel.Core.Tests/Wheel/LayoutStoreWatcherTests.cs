using System.Threading.Channels;
using DropWheel.Core.Conversion;
using DropWheel.Core.Wheel;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace DropWheel.Core.Tests.Wheel;

public sealed class LayoutStoreWatcherTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-layout-watch-").FullName;
    private string File_ => Path.Combine(_dir, "layout.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("create")]
    [InlineData("write")]
    [InlineData("replace")]
    [InlineData("delete")]
    public async Task File_events_reload_without_an_explicit_reload_call(string edit)
    {
        if (edit != "create")
        {
            WriteLayout(File_, "tool.compress-image");
        }

        using var store = new LayoutStore(File_);
        var changed = new TaskCompletionSource<WheelLayout>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += layout => changed.TrySetResult(layout);
        store.Watch();

        switch (edit)
        {
            case "replace":
                var replacement = Path.Combine(_dir, "replacement.json");
                WriteLayout(replacement, "tool.ocr");
                File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(File_).AddSeconds(2));
                File.Move(replacement, File_, overwrite: true);
                break;
            case "delete":
                File.Delete(File_);
                break;
            default:
                WriteLayout(File_, "tool.ocr");
                break;
        }

        var loaded = await changed.Task.WaitAsync(Timeout);
        loaded.Should().BeSameAs(store.Current);
        Item(loaded).Should().Be(edit == "delete" ? null : "tool.ocr");
        store.LoadProblem.Should().BeNull();
    }

    [Fact]
    public async Task File_events_debounce_bursts_and_load_the_latest_edit()
    {
        WriteLayout(File_, "tool.compress-image");
        var time = new ObservedTime();
        using var store = new LayoutStore(File_, time);
        var changed = new TaskCompletionSource<WheelLayout>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += layout => changed.TrySetResult(layout);
        store.Watch();

        WriteLayout(File_, "tool.half-size");
        await time.NextSchedule();
        time.Clock.Advance(TimeSpan.FromMilliseconds(299));
        changed.Task.IsCompleted.Should().BeFalse();

        time.DrainSchedules();
        WriteLayout(File_, "tool.ocr");
        await time.NextSchedule();
        time.Clock.Advance(TimeSpan.FromMilliseconds(1));
        changed.Task.IsCompleted.Should().BeFalse("the second edit postpones the first edit's deadline");
        time.Clock.Advance(TimeSpan.FromMilliseconds(298));
        changed.Task.IsCompleted.Should().BeFalse();

        await time.RunPendingCallbacks();
        Item(await changed.Task.WaitAsync(Timeout)).Should().Be("tool.ocr");
        Item(store.Current).Should().Be("tool.ocr");
    }

    [Fact]
    public async Task The_watcher_does_not_notify_again_for_our_own_save()
    {
        var time = new ObservedTime();
        using var store = new LayoutStore(File_, time);
        var changes = 0;
        store.Changed += _ => Interlocked.Increment(ref changes);
        store.Watch();

        var saved = store.Update(layout => layout.With(WheelMode.Tools, MediaKind.Image, [new("tool.ocr")]));
        changes.Should().Be(1);
        await time.NextSchedule();
        await time.RunPendingCallbacks();

        changes.Should().Be(1);
        store.Current.Should().BeSameAs(saved);
    }

    [Fact]
    public async Task A_file_event_retries_a_locked_file_and_recovers_without_another_write()
    {
        WriteLayout(File_, "tool.compress-image");
        var time = new ObservedTime();
        using var store = new LayoutStore(File_, time);
        var previous = store.Current;
        var changed = new TaskCompletionSource<WheelLayout>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += layout => changed.TrySetResult(layout);
        store.Watch();

        var editedStamp = File.GetLastWriteTimeUtc(File_).AddSeconds(2);
        using (var held = new FileStream(File_, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(Layout("tool.ocr"));
            held.SetLength(0);
            held.Write(bytes);
            held.Flush(flushToDisk: true);
            File.SetLastWriteTimeUtc(held.SafeFileHandle, editedStamp);
            await time.NextSchedule();
            await time.RunPendingCallbacks();
            store.Current.Should().BeSameAs(previous);
            store.LoadProblem.Should().NotBeNull();
            changed.Task.IsCompleted.Should().BeFalse();
        }

        await time.RunPendingCallbacks();
        Item(await changed.Task.WaitAsync(Timeout)).Should().Be("tool.ocr");
        store.LoadProblem.Should().BeNull();
    }

    private static string? Item(WheelLayout layout) => layout.SlotsFor(WheelMode.Tools, MediaKind.Image)?.Single()?.Item;

    private static string Layout(string item) => $$$"""{"version":1,"wheels":{"tools":{"image":[{"item":"{{{item}}}"}]}},"installed":[]}""";

    private static void WriteLayout(string path, string item)
    {
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.UtcNow;
        File.WriteAllText(path, Layout(item));
        File.SetLastWriteTimeUtc(path, stamp.AddSeconds(2));
    }

    // Observe real watcher events, but advance the debounce timer without wall-clock sleeps.
    private sealed class ObservedTime : TimeProvider
    {
        private readonly Channel<(TimeSpan Due, TimeSpan Period)> _scheduled = Channel.CreateUnbounded<(TimeSpan, TimeSpan)>();
        private int _callbacks;
        public FakeTimeProvider Clock { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new ObservedTimer(Clock.CreateTimer(s => { callback(s); Interlocked.Increment(ref _callbacks); }, state, dueTime, period), _scheduled.Writer);

        public async Task NextSchedule()
        {
            var schedule = await _scheduled.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            schedule.Should().Be((TimeSpan.FromMilliseconds(300), global::System.Threading.Timeout.InfiniteTimeSpan));
        }

        public void DrainSchedules()
        {
            while (_scheduled.Reader.TryRead(out _)) { }
        }

        public async Task RunPendingCallbacks()
        {
            var before = Volatile.Read(ref _callbacks);
            using var deadline = new CancellationTokenSource(Timeout);
            while (Volatile.Read(ref _callbacks) == before)
            {
                Clock.Advance(TimeSpan.FromMilliseconds(300));
                if (Volatile.Read(ref _callbacks) == before)
                {
                    // A late OS event can reset the timer while the fake clock advances.
                    await Task.Delay(10, deadline.Token);
                }
            }
        }

        private sealed class ObservedTimer(ITimer timer, ChannelWriter<(TimeSpan Due, TimeSpan Period)> scheduled) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                var changed = timer.Change(dueTime, period);
                scheduled.TryWrite((dueTime, period));
                return changed;
            }

            public void Dispose() => timer.Dispose();
            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }
}
