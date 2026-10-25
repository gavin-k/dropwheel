using System.Collections.Concurrent;
using System.Threading.Channels;
using DropWheel.Core.Conversion;

namespace DropWheel.Core.Jobs;

/// <summary>
/// Runs conversion jobs one at a time, in the order they were enqueued, on a background worker.
/// <see cref="JobChanged"/> is raised on whichever thread changed the job; UI code marshals it itself.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "It is a queue; the name is used throughout the docs.")]
public sealed class JobQueue : IAsyncDisposable
{
    private readonly ConversionGraph _graph;
    private readonly UndoLedger _undo;
    private readonly TimeProvider _time;
    private readonly Channel<Job> _channel = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancels = new();
    private readonly List<Job> _jobs = [];
    private readonly Lock _gate = new();
    private readonly Task _worker;
    private TaskCompletionSource _idle = new();
    private int _outstanding;

    public JobQueue(ConversionGraph graph, UndoLedger undo, TimeProvider? time = null)
    {
        _graph = graph;
        _undo = undo;
        _time = time ?? TimeProvider.System;
        _idle.SetResult();
        _worker = Task.Run(RunWorkerAsync);
    }

    public event Action<Job>? JobChanged;

    /// <summary>Snapshot, oldest first.</summary>
    public IReadOnlyList<Job> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs];
            }
        }
    }

    public Job Enqueue(ConversionRequest request, string label)
    {
        var job = new Job { Request = request, Label = label };
        lock (_gate)
        {
            _jobs.Add(job);
            if (_outstanding++ == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        _cancels[job.Id] = new CancellationTokenSource();
        Raise(job);
        ObjectDisposedException.ThrowIf(!_channel.Writer.TryWrite(job), this);

        return job;
    }

    /// <summary>Cancels a pending or running job. Returns false if it has already finished.</summary>
    public bool Cancel(Guid id)
    {
        if (!_cancels.TryGetValue(id, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Runs a failed or canceled job again, as a new job with the same request and label.</summary>
    /// <returns>The new job, or null if the job is in any other state.</returns>
    public Job? Retry(Guid id)
        => Jobs.FirstOrDefault(j => j.Id == id) is { State: JobState.Failed or JobState.Canceled } job ? Enqueue(job.Request, job.Label) : null;

    /// <summary>Recycles a finished job's outputs. Returns false if the job is not in a state that can be undone.</summary>
    public async Task<bool> UndoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = Jobs.FirstOrDefault(j => j.Id == id);
        if (job is not { State: JobState.Done } || !await _undo.UndoAsync(job, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        job.State = JobState.Undone;
        Raise(job);
        return true;
    }

    /// <summary>Completes when no job is pending or running.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _idle.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        foreach (var cts in _cancels.Values)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        await _worker.ConfigureAwait(false);
    }

    private async Task RunWorkerAsync()
    {
        await foreach (var job in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await RunAsync(job).ConfigureAwait(false);

            TaskCompletionSource? idle = null;
            lock (_gate)
            {
                if (--_outstanding == 0)
                {
                    idle = _idle;
                }
            }

            idle?.TrySetResult();
        }
    }

    private async Task RunAsync(Job job)
    {
        var cts = _cancels[job.Id];
        try
        {
            cts.Token.ThrowIfCancellationRequested();
            var availability = _graph.Check(job.Request);
            if (!availability.IsAvailable)
            {
                Finish(job, JobState.Failed, error: availability.Reason);
                return;
            }

            var from = FormatRegistry.FromPath(job.Request.SourcePaths[0])!;
            var to = FormatRegistry.FromExtension(job.Request.TargetFormatId)!;
            var converter = _graph.Resolve(from.Id, to.Id)!;

            job.State = JobState.Running;
            Raise(job);
            var progress = new InlineProgress(p =>
            {
                job.Progress = p;
                Raise(job);
            });
            var result = await converter.RunAsync(job.Request, progress, cts.Token).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                Finish(job, JobState.Failed, error: result.Error ?? "转换失败。");
            }
            else if (result.OutputPaths.Count == 0)
            {
                Finish(job, JobState.Skipped, detail: "目标文件已存在，按重名策略跳过。");
            }
            else
            {
                job.Outputs = result.OutputPaths;
                job.Undo = new UndoPlan.RecycleOutputs(result.OutputPaths);
                job.Progress = 1;
                _undo.Record(job);
                Finish(job, JobState.Done);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Finish(job, JobState.Canceled);
        }
        catch (Exception ex)
        {
            Finish(job, JobState.Failed, error: ex.Message);
        }
        finally
        {
            _cancels.TryRemove(job.Id, out _);
            cts.Dispose();
        }
    }

    private void Finish(Job job, JobState state, string? detail = null, string? error = null)
    {
        job.State = state;
        job.Detail = detail;
        job.Error = error;
        job.FinishedAt = _time.GetLocalNow();
        Raise(job);
    }

    private void Raise(Job job) => JobChanged?.Invoke(job);
}
