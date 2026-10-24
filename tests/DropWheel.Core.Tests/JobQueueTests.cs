using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class JobQueueTests
{
    private readonly RecordingFiles _files = new();

    private (JobQueue Queue, List<(Guid Id, JobState State)> Changes) Create(Func<ConversionRequest, IProgress<double>, CancellationToken, Task<ConversionResult>> run)
    {
        var graph = new ConversionGraph();
        graph.Register(new TestConverter([new("jpg", "png")], run));
        var queue = new JobQueue(graph, new UndoLedger(_files));
        var changes = new List<(Guid, JobState)>();
        queue.JobChanged += j =>
        {
            lock (changes)
            {
                changes.Add((j.Id, j.State));
            }
        };
        return (queue, changes);
    }

    private static ConversionRequest Jpg(string name = "a") => new([$"/in/{name}.jpg"], "png", "");

    [Fact]
    public async Task Successful_job_is_done_with_a_recycle_undo_plan()
    {
        var (queue, changes) = Create((r, p, _) =>
        {
            p.Report(0.5);
            return Task.FromResult(new ConversionResult(true, ["/in/a.png"]));
        });
        await using var _ = queue;

        var job = queue.Enqueue(Jpg(), "a.jpg → PNG");
        await queue.WhenIdleAsync();

        job.State.Should().Be(JobState.Done);
        job.Outputs.Should().Equal("/in/a.png");
        job.Progress.Should().Be(1);
        job.Undo.Should().BeOfType<UndoPlan.RecycleOutputs>().Which.Paths.Should().Equal("/in/a.png");
        job.FinishedAt.Should().NotBeNull();
        changes.Select(c => c.State).Should().ContainInOrder(JobState.Pending, JobState.Running, JobState.Running, JobState.Done);
    }

    [Fact]
    public async Task Undo_recycles_outputs_once()
    {
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(true, ["/in/a.png"])));
        await using var _q = queue;
        var job = queue.Enqueue(Jpg(), "x");
        await queue.WhenIdleAsync();

        (await queue.UndoAsync(job.Id)).Should().BeTrue();
        (await queue.UndoAsync(job.Id)).Should().BeFalse();

        job.State.Should().Be(JobState.Undone);
        _files.Recycled.Should().Equal("/in/a.png");
    }

    [Fact]
    public async Task Failed_undo_leaves_the_job_done_and_retryable()
    {
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(true, ["/in/a.png"])));
        await using var _q = queue;
        var job = queue.Enqueue(Jpg(), "x");
        await queue.WhenIdleAsync();

        _files.FailNext = true;
        await queue.Invoking(q => q.UndoAsync(job.Id)).Should().ThrowAsync<IOException>();
        job.State.Should().Be(JobState.Done);
        (await queue.UndoAsync(job.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Engine_failure_marks_the_job_failed()
    {
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(false, [], "坏文件")));
        await using var _q = queue;
        var job = queue.Enqueue(Jpg(), "x");
        await queue.WhenIdleAsync();

        job.State.Should().Be(JobState.Failed);
        job.Error.Should().Be("坏文件");
        (await queue.UndoAsync(job.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Engine_exception_marks_the_job_failed()
    {
        var (queue, _) = Create((_, _, _) => throw new InvalidOperationException("boom"));
        await using var _q = queue;
        var job = queue.Enqueue(Jpg(), "x");
        await queue.WhenIdleAsync();

        job.State.Should().Be(JobState.Failed);
        job.Error.Should().Be("boom");
    }

    [Fact]
    public async Task No_outputs_means_skipped()
    {
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(true, [])));
        await using var _q = queue;
        var job = queue.Enqueue(Jpg(), "x");
        await queue.WhenIdleAsync();

        job.State.Should().Be(JobState.Skipped);
    }

    [Fact]
    public async Task Unavailable_target_fails_without_running_the_engine()
    {
        var ran = false;
        var (queue, _) = Create((_, _, _) =>
        {
            ran = true;
            return Task.FromResult(new ConversionResult(true, []));
        });
        await using var _q = queue;
        var job = queue.Enqueue(new(["/in/a.jpg"], "raw", ""), "x");
        await queue.WhenIdleAsync();

        job.State.Should().Be(JobState.Failed);
        job.Error.Should().NotBeNullOrEmpty();
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_stops_a_running_job_and_a_pending_one_never_starts()
    {
        var started = new List<string>();
        var running = new TaskCompletionSource();
        var (queue, _) = Create(async (r, _, ct) =>
        {
            lock (started)
            {
                started.Add(r.SourcePaths[0]);
            }

            running.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new ConversionResult(true, []);
        });
        await using var _q = queue;

        var first = queue.Enqueue(Jpg("a"), "a");
        var second = queue.Enqueue(Jpg("b"), "b");
        await running.Task;
        queue.Cancel(second.Id).Should().BeTrue();
        queue.Cancel(first.Id).Should().BeTrue();
        await queue.WhenIdleAsync();

        first.State.Should().Be(JobState.Canceled);
        second.State.Should().Be(JobState.Canceled);
        started.Should().Equal("/in/a.jpg");
        queue.Cancel(first.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Jobs_run_one_at_a_time_in_order()
    {
        var active = 0;
        var maxActive = 0;
        var order = new List<string>();
        var (queue, _) = Create(async (r, _, _) =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            await Task.Delay(10, CancellationToken.None);
            lock (order)
            {
                order.Add(Path.GetFileNameWithoutExtension(r.SourcePaths[0]));
            }

            Interlocked.Decrement(ref active);
            return new ConversionResult(true, [$"/in/{order.Count}.png"]);
        });
        await using var _q = queue;

        foreach (var n in new[] { "a", "b", "c", "d" })
        {
            queue.Enqueue(Jpg(n), n);
        }

        await queue.WhenIdleAsync();
        order.Should().Equal("a", "b", "c", "d");
        maxActive.Should().Be(1);
        queue.Jobs.Should().OnlyContain(j => j.State == JobState.Done);
    }

    [Fact]
    public async Task Idle_task_is_already_complete_with_no_jobs()
    {
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(true, [])));
        await using var _q = queue;
        queue.WhenIdleAsync().IsCompleted.Should().BeTrue();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class RecordingFiles : IFileOperations
    {
        public List<string> Recycled { get; } = [];

        public bool FailNext { get; set; }

        public Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("locked");
            }

            Recycled.AddRange(paths);
            return Task.CompletedTask;
        }

        public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
