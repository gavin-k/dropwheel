namespace DropWheel.Core.Jobs;

/// <summary>
/// Remembers finished jobs that can be reversed and reverses them through <see cref="IFileOperations"/>.
/// Source files are never touched: a conversion is undone by recycling what it produced.
/// </summary>
public sealed class UndoLedger(IFileOperations files)
{
    private readonly List<Job> _entries = [];

    /// <summary>What it reverses through; jobs recycle the items they replace through it too.</summary>
    public IFileOperations Files => files;
    private readonly Lock _gate = new();

    /// <summary>The newest job that can still be undone.</summary>
    public Job? Latest
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count > 0 ? _entries[^1] : null;
            }
        }
    }

    public void Record(Job job)
    {
        if (job.Undo is UndoPlan.None)
        {
            return;
        }

        lock (_gate)
        {
            _entries.Add(job);
        }
    }

    /// <summary>Reverses <paramref name="job"/> once. Returns false if it was never recorded or is already undone.</summary>
    public async Task<bool> UndoAsync(Job job, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_entries.Remove(job))
            {
                return false;
            }
        }

        try
        {
            await (job.Undo switch
            {
                UndoPlan.RecycleOutputs r => files.RecycleAsync(r.Paths, cancellationToken),
                UndoPlan.MoveBack m => files.MoveAsync([.. m.Moves.Select(x => new FileMove(x.To, x.From))], cancellationToken),
                _ => throw new NotSupportedException($"Undo for {job.Undo.GetType().Name} is not implemented yet."),
            }).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _entries.Add(job);
            }

            throw;
        }

        return true;
    }
}
