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
    private readonly IConflictPrompt? _prompt;
    private readonly Channel<Job> _channel = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancels = new();
    private readonly List<Job> _jobs = [];
    private readonly Lock _gate = new();
    private readonly Task _worker;
    private TaskCompletionSource _idle = new();
    private int _outstanding;

    /// <param name="prompt">Asks about taken names under <see cref="ConflictPolicy.Ask"/>; without one they are numbered.</param>
    public JobQueue(ConversionGraph graph, UndoLedger undo, TimeProvider? time = null, IConflictPrompt? prompt = null)
    {
        _graph = graph;
        _undo = undo;
        _time = time ?? TimeProvider.System;
        _prompt = prompt;
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

    /// <param name="runner">Runs the request; null converts it with whatever the graph resolves for its formats.</param>
    /// <param name="countsTowardQuota">False for file actions, which never count against the daily quota.</param>
    public Job Enqueue(ConversionRequest request, string label, IJobRunner? runner = null, bool countsTowardQuota = true)
    {
        var job = new Job { Request = request, Label = label, Runner = runner, CountsTowardQuota = countsTowardQuota };
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

    /// <summary>Runs a failed or canceled job again, as a new job with the same request, label and runner.</summary>
    /// <returns>The new job, or null if the job is in any other state.</returns>
    public Job? Retry(Guid id)
        => Jobs.FirstOrDefault(j => j.Id == id) is { State: JobState.Failed or JobState.Canceled } job
            ? Enqueue(job.Request, job.Label, job.Runner, job.CountsTowardQuota)
            : null;

    /// <summary>Reverses a finished job by its <see cref="Job.Undo"/> plan. Returns false if the job is not in a state that can be undone.</summary>
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
        var conflicts = new JobConflicts(_undo.Files, _prompt, job.Request.SourcePaths, cts.Token);
        try
        {
            cts.Token.ThrowIfCancellationRequested();
            var availability = job.Runner?.Check(job.Request) ?? _graph.Check(job.Request);
            if (!availability.IsAvailable)
            {
                Finish(job, JobState.Failed, error: availability.Reason);
                return;
            }

            var runner = job.Runner ?? _graph.Resolve(
                FormatRegistry.FromPath(job.Request.SourcePaths[0])!.Id,
                FormatRegistry.FromExtension(job.Request.TargetFormatId)!.Id)!;

            job.State = JobState.Running;
            Raise(job);
            var progress = new InlineProgress(p =>
            {
                job.Progress = p;
                Raise(job);
            });
            // The request the job keeps has no Conflicts, so a retry starts with fresh answers.
            var request = job.Request with { Conflicts = conflicts, Written = new(StringComparer.OrdinalIgnoreCase) };
            var result = await runner.RunAsync(request, progress, cts.Token).ConfigureAwait(false);
            job.Replaced = conflicts.Replaced;

            if (!result.Succeeded)
            {
                Finish(job, JobState.Failed, error: result.Error ?? L.T("转换失败。"));
            }
            else if (result.Skipped)
            {
                Finish(job, JobState.Skipped, detail: result.Detail ?? L.T("目标文件已存在，按重名策略跳过。"));
            }
            else
            {
                job.Outputs = result.OutputPaths;
                job.Undo = result.Undo ?? (result.OutputPaths.Count > 0 ? new UndoPlan.RecycleOutputs(result.OutputPaths) : new UndoPlan.None());
                job.Progress = 1;
                _undo.Record(job, request.Written);
                Finish(job, JobState.Done, detail: result.Detail);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            job.Replaced = conflicts.Replaced;
            Finish(job, JobState.Canceled);
        }
        catch (Exception ex)
        {
            job.Replaced = conflicts.Replaced;
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
