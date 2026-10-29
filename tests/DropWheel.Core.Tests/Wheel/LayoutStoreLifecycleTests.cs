using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests.Wheel;

public sealed class LayoutStoreLifecycleTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-layout-lifecycle-").FullName;
    private string File_ => Path.Combine(_dir, "layout.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Watching_twice_keeps_the_same_timer()
    {
        var time = new ControlledTime();
        using var store = new LayoutStore(File_, time);
        store.Watch();
        store.Watch();

        time.Created.Should().Be(1);
    }

    [Fact]
    public async Task A_dequeued_callback_cannot_schedule_a_retry_after_disposal()
    {
        var time = new ControlledTime();
        using var store = new LayoutStore(File_, time);
        var previous = store.Current;
        store.Watch();
        WriteExternalLayout();
        using var held = new FileStream(File_, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await time.Changed.Task.WaitAsync(Timeout);
        var callback = time.DequeueCallback();

        store.Dispose();
        callback();

        store.Current.Should().BeSameAs(previous);
        time.ChangesAfterDisposal.Should().Be(0);
        time.Pending.Should().BeFalse();
    }

    [Fact]
    public async Task Disposal_waits_for_a_file_event_that_is_scheduling_the_timer()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new ControlledTime { BeforeChange = () => { entered.TrySetResult(); release.Task.Wait(Timeout); } };
        using var store = new LayoutStore(File_, time);
        Task disposing;
        bool finishedWhileScheduling;
        try
        {
            store.Watch();
            WriteExternalLayout();
            await entered.Task.WaitAsync(Timeout);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposing = Task.Run(() => { started.SetResult(); store.Dispose(); });
            await started.Task.WaitAsync(Timeout);
            finishedWhileScheduling = await Task.WhenAny(disposing, Task.Delay(100)) == disposing;
        }
        finally
        {
            release.TrySetResult();
        }

        await disposing.WaitAsync(Timeout);
        finishedWhileScheduling.Should().BeFalse();
        time.DisposedDuringChange.Should().BeFalse();
        time.ChangesAfterDisposal.Should().Be(0);
        time.Pending.Should().BeFalse();
    }

    private void WriteExternalLayout() => File.WriteAllText(File_, """{"version":1,"wheels":{},"installed":["user.external"]}""");

    private sealed class ControlledTime : TimeProvider, ITimer
    {
        private TimerCallback? _callback;
        private object? _state;
        private volatile bool _changing;
        private volatile bool _disposed;
        public int Created { get; private set; }
        public int ChangesAfterDisposal { get; private set; }
        public bool DisposedDuringChange { get; private set; }
        public volatile bool Pending;
        public Action? BeforeChange { get; init; }
        public TaskCompletionSource Changed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Created++;
            _callback = callback;
            _state = state;
            return this;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _changing = true;
            BeforeChange?.Invoke();
            if (_disposed)
            {
                ChangesAfterDisposal++;
            }

            Pending = !_disposed && dueTime != global::System.Threading.Timeout.InfiniteTimeSpan;
            _changing = false;
            Changed.TrySetResult();
            return !_disposed;
        }

        public Action DequeueCallback()
        {
            Pending = false;
            return () => _callback!(_state);
        }

        public void Dispose()
        {
            DisposedDuringChange |= _changing;
            _disposed = true;
            Pending = false;
        }

        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
