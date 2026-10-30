using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Tools;
using DropWheel.Core.Wheel;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public sealed class JobQueueTests : IDisposable
{
    private readonly RecordingFiles _files = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("dropwheel-queue-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Output(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "generated");
        return path;
    }

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
    public async Task A_script_tool_runs_through_the_queue_and_its_output_can_be_undone()
    {
        var source = Path.Combine(_dir, "a.jpg");
        File.WriteAllText(source, "source");
        var toolDir = Directory.CreateDirectory(Path.Combine(_dir, "tool")).FullName;
        File.WriteAllText(Path.Combine(toolDir, "clean.exe"), "");
        var runner = new Tools.FakeProcessRunner
        {
            Behavior = (start, print, _) =>
            {
                print("progress 0.5");
                File.WriteAllText(start.Arguments[1], "cleaned");
                return Task.FromResult(new ProcessOutcome(0, ""));
            },
        };
        var manifest = new ToolManifest(1, "user.clean", new("清理"), new(""), null, WheelMode.Tools, null, "script",
            new("clean.exe", ["{input}", "{output}"], true, new("new-file", "same", "-clean"), 600), null, "recycle");
        var tool = new ScriptTool(manifest, toolDir, runner, new CommandResolver(File.Exists, [], []));
        var (queue, changes) = Create((_, _, _) => throw new InvalidOperationException("the graph is not consulted"));
        await using var _ = queue;

        var job = queue.Enqueue(new([source], tool.Id, ""), "a.jpg · 清理", tool);
        await queue.WhenIdleAsync();

        var output = Path.Combine(_dir, "a-clean.jpg");
        job.State.Should().Be(JobState.Done);
        job.Outputs.Should().Equal(output);
        job.Progress.Should().Be(1);
        job.Undo.Should().BeOfType<UndoPlan.RecycleOutputs>().Which.Paths.Should().Equal(output);
        File.ReadAllText(output).Should().Be("cleaned");
        changes.Select(c => c.State).Should().ContainInOrder(JobState.Pending, JobState.Running, JobState.Done);

        (await queue.UndoAsync(job.Id)).Should().BeTrue();
        _files.Recycled.Should().Equal(output);
        Directory.EnumerateFileSystemEntries(_dir).Select(Path.GetFileName).Should().NotContain(n => n!.EndsWith(OutputFiles.TempSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Undo_recycles_outputs_once()
    {
        var output = Output("a.png");
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(true, [output])));
        await using var _q = queue;
        var job = queue.Enqueue(Jpg(), "x");
        await queue.WhenIdleAsync();

        (await queue.UndoAsync(job.Id)).Should().BeTrue();
        (await queue.UndoAsync(job.Id)).Should().BeFalse();

        job.State.Should().Be(JobState.Undone);
        _files.Recycled.Should().Equal(output);
    }

    [Fact]
    public async Task Failed_undo_leaves_the_job_done_and_retryable()
    {
        var output = Output("a.png");
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(true, [output])));
        await using var _q = queue;
        var job = queue.Enqueue(Jpg(), "x");
        await queue.WhenIdleAsync();

        _files.FailNext = true;
        await queue.Invoking(q => q.UndoAsync(job.Id)).Should().ThrowAsync<IOException>();
        job.State.Should().Be(JobState.Done);
        (await queue.UndoAsync(job.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Undo_refuses_a_same_name_replacement_and_leaves_the_job_done()
    {
        var output = Output("a.png");
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(true, [output])));
        await using var _q = queue;
        var job = queue.Enqueue(Jpg(), "x");
        await queue.WhenIdleAsync();
        File.Move(output, output + ".original");
        File.WriteAllText(output, "user content");

        await queue.Invoking(q => q.UndoAsync(job.Id)).Should().ThrowAsync<IOException>();
        job.State.Should().Be(JobState.Done);
        _files.Recycled.Should().BeEmpty();
        File.ReadAllText(output).Should().Be("user content");
    }

    [Fact]
    public async Task Undo_refuses_a_recreated_identical_file_even_with_preserved_windows_timestamps()
    {
        var output = Output("a.png");
        var created = File.GetCreationTimeUtc(output);
        var modified = File.GetLastWriteTimeUtc(output);
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(true, [output])));
        await using var _q = queue;
        var job = queue.Enqueue(Jpg(), "x");
        await queue.WhenIdleAsync();
        File.Move(output, output + ".original");
        File.WriteAllText(output, "generated");
        File.SetLastWriteTimeUtc(output, modified);
        if (OperatingSystem.IsWindows())
        {
            File.SetCreationTimeUtc(output, created);
        }

        await queue.Invoking(q => q.UndoAsync(job.Id)).Should().ThrowAsync<IOException>();
        _files.Recycled.Should().BeEmpty();
        File.ReadAllText(output).Should().Be("generated");
    }

    [Fact]
    public async Task Undo_uses_the_fingerprint_from_commit_not_from_job_completion()
    {
        var source = Path.Combine(_dir, "a.jpg");
        var (queue, _) = Create(async (r, _, ct) =>
        {
            var output = await OutputFiles.WriteAsync(r, source, 0, DateTimeOffset.Now,
                temp => File.WriteAllTextAsync(temp, "ours", ct), cancellationToken: ct);
            File.WriteAllText(output!, "user");
            return ConversionResult.Written([output!]);
        });
        await using var _q = queue;
        var job = queue.Enqueue(new([source], "png", _dir), "x");
        await queue.WhenIdleAsync();

        await queue.Invoking(q => q.UndoAsync(job.Id)).Should().ThrowAsync<IOException>();
        _files.Recycled.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_after_commit_but_before_runner_completion_rolls_back_and_marks_canceled()
    {
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Path.Combine(_dir, "a.jpg");
        var (queue, _) = Create(async (r, _, ct) =>
        {
            var output = await OutputFiles.WriteAsync(r, source, 0, DateTimeOffset.Now,
                temp => File.WriteAllTextAsync(temp, "ours", ct), cancellationToken: ct);
            committed.SetResult();
            await finish.Task;
            return ConversionResult.Written([output!]);
        });
        await using var _q = queue;
        var job = queue.Enqueue(new([source], "png", _dir), "x");
        await committed.Task;
        queue.Cancel(job.Id).Should().BeTrue();
        finish.SetResult();
        await queue.WhenIdleAsync();

        job.State.Should().Be(JobState.Canceled);
        Directory.GetFileSystemEntries(_dir).Should().BeEmpty();
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
    public async Task An_engine_that_wrote_nothing_is_skipped()
    {
        var (queue, _) = Create((_, _, _) => Task.FromResult(ConversionResult.Written([])));
        await using var _q = queue;
        var job = queue.Enqueue(Jpg(), "x");
        await queue.WhenIdleAsync();

        job.State.Should().Be(JobState.Skipped);
        job.Detail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_job_with_no_outputs_that_is_not_skipped_is_done_with_its_detail_and_no_undo()
    {
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(true, []) { Detail = "已复制路径" }));
        await using var _q = queue;
        var job = queue.Enqueue(Jpg(), "x");
        await queue.WhenIdleAsync();

        job.State.Should().Be(JobState.Done);
        job.Detail.Should().Be("已复制路径");
        job.Undo.Should().BeOfType<UndoPlan.None>();
        (await queue.UndoAsync(job.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_move_back_plan_is_kept_and_undo_moves_the_items_back()
    {
        var a = Output("a.jpg");
        var b = Output("b");
        var moves = new[] { new FileMove("/in/a.jpg", a), new FileMove("/in/b", b) };
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(true, [a, b]) { Undo = new UndoPlan.MoveBack(moves) }));
        await using var _q = queue;
        var job = queue.Enqueue(Jpg(), "x");
        await queue.WhenIdleAsync();

        job.Undo.Should().BeOfType<UndoPlan.MoveBack>();
        (await queue.UndoAsync(job.Id)).Should().BeTrue();

        _files.Recycled.Should().BeEmpty();
        _files.Moved.Should().Equal(new FileMove(a, "/in/a.jpg"), new FileMove(b, "/in/b"));
    }

    [Fact]
    public async Task A_job_runner_runs_instead_of_the_graph_and_retry_keeps_it()
    {
        var graphRan = false;
        var (queue, _) = Create((_, _, _) =>
        {
            graphRan = true;
            return Task.FromResult(new ConversionResult(true, []));
        });
        await using var _q = queue;
        var runs = 0;
        var runner = new TestRunner((_, _, _) => Task.FromResult(++runs == 1 ? new ConversionResult(false, [], "占用") : new ConversionResult(true, ["/in/a.zip"])));

        // "action.x" is no format the graph knows, so only the runner can take it.
        var job = queue.Enqueue(new(["/in/a.jpg"], "action.x", ""), "x", runner, countsTowardQuota: false);
        await queue.WhenIdleAsync();
        job.State.Should().Be(JobState.Failed);

        var retry = queue.Retry(job.Id)!;
        await queue.WhenIdleAsync();

        retry.State.Should().Be(JobState.Done);
        retry.Runner.Should().BeSameAs(runner);
        retry.CountsTowardQuota.Should().BeFalse();
        runs.Should().Be(2);
        graphRan.Should().BeFalse();
    }

    [Fact]
    public async Task An_unavailable_runner_fails_the_job_with_its_reason()
    {
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(true, [])));
        await using var _q = queue;
        var runner = new TestRunner((_, _, _) => throw new InvalidOperationException("ran"), Availability.Unavailable("一次只能重命名一项"));
        var job = queue.Enqueue(new(["/in/a.jpg", "/in/b.jpg"], "action.x", ""), "x", runner);
        await queue.WhenIdleAsync();

        job.State.Should().Be(JobState.Failed);
        job.Error.Should().Be("一次只能重命名一项");
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
    public async Task Retry_runs_a_failed_job_again_as_a_new_job()
    {
        var attempts = 0;
        var (queue, _) = Create((_, _, _) => Task.FromResult(Interlocked.Increment(ref attempts) == 1
            ? new ConversionResult(false, [], "文件被占用")
            : new ConversionResult(true, ["/in/a.png"])));
        await using var _q = queue;
        var failed = queue.Enqueue(Jpg(), "a.jpg → PNG");
        await queue.WhenIdleAsync();

        var retry = queue.Retry(failed.Id);
        await queue.WhenIdleAsync();

        retry.Should().NotBeNull();
        retry!.Id.Should().NotBe(failed.Id);
        retry.Label.Should().Be("a.jpg → PNG");
        retry.State.Should().Be(JobState.Done);
        failed.State.Should().Be(JobState.Failed);
        queue.Retry(retry.Id).Should().BeNull("a job that worked has nothing to retry");
    }

    [Fact]
    public async Task Retry_runs_a_canceled_job_again()
    {
        var running = new TaskCompletionSource();
        var (queue, _) = Create(async (_, _, ct) =>
        {
            if (running.TrySetResult())
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return new ConversionResult(true, ["/in/a.png"]);
        });
        await using var _q = queue;
        var canceled = queue.Enqueue(Jpg(), "x");
        await running.Task;
        queue.Cancel(canceled.Id);
        await queue.WhenIdleAsync();

        var retry = queue.Retry(canceled.Id);
        await queue.WhenIdleAsync();

        canceled.State.Should().Be(JobState.Canceled);
        retry!.State.Should().Be(JobState.Done);
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
    public async Task A_script_job_does_not_hold_up_the_engine_job_behind_it()
    {
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(true, [])));
        await using var _q = queue;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new TestRunner(async (_, _, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return new ConversionResult(true, []);
        }, lane: JobLane.Script);
        var engineDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.JobChanged += j =>
        {
            if (j.Label == "quick" && j.State == JobState.Done)
            {
                engineDone.TrySetResult();
            }
        };

        var slow = queue.Enqueue(Jpg("slow"), "slow", script);
        var quick = queue.Enqueue(Jpg("quick"), "quick");

        await engineDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        slow.State.Should().Be(JobState.Running);
        slow.Lane.Should().Be(JobLane.Script);
        quick.Lane.Should().Be(JobLane.Engine);
        queue.WhenIdleAsync().IsCompleted.Should().BeFalse();

        release.SetResult();
        await queue.WhenIdleAsync();
        slow.State.Should().Be(JobState.Done);
    }

    [Fact]
    public async Task Script_jobs_still_run_one_at_a_time_among_themselves()
    {
        var (queue, _) = Create((_, _, _) => Task.FromResult(new ConversionResult(true, [])));
        await using var _q = queue;
        var active = 0;
        var maxActive = 0;
        var order = new List<string>();
        var script = new TestRunner(async (r, _, _) =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            lock (order)
            {
                order.Add(Path.GetFileNameWithoutExtension(r.SourcePaths[0]));
            }

            await Task.Delay(20);
            Interlocked.Decrement(ref active);
            return new ConversionResult(true, []);
        }, lane: JobLane.Script);

        foreach (var n in new[] { "a", "b", "c" })
        {
            queue.Enqueue(Jpg(n), n, script);
        }

        await queue.WhenIdleAsync();
        order.Should().Equal("a", "b", "c");
        maxActive.Should().Be(1);
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

    private sealed class TestRunner(Func<ConversionRequest, IProgress<double>, CancellationToken, Task<ConversionResult>> run, Availability? availability = null, JobLane lane = JobLane.Engine) : IJobRunner
    {
        public Availability Check(ConversionRequest request) => availability ?? Availability.Available;

        public JobLane Lane => lane;

        public Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken) => run(request, progress, cancellationToken);
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

        public List<FileMove> Moved { get; } = [];

        public Task MoveAsync(IReadOnlyList<FileMove> moves, CancellationToken cancellationToken = default)
        {
            Moved.AddRange(moves);
            return Task.CompletedTask;
        }

        public Task CopyAsync(IReadOnlyList<FileMove> copies, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
