namespace DropWheel.Core.Jobs;

/// <summary>Asks the user what to do with an output whose name is taken, for <see cref="ConflictPolicy.Ask"/>.</summary>
public interface IConflictPrompt
{
    /// <summary>Asks about <paramref name="target"/>, which already exists. Closing the question is a skip.</summary>
    /// <param name="more">The job has more items after this one, so the answer may be kept for them.</param>
    /// <returns>Replace, number or skip; any other policy counts as skip.</returns>
    Task<ConflictAnswer> AskAsync(string target, bool more, CancellationToken cancellationToken);
}

/// <param name="Policy"><see cref="ConflictPolicy.OverwriteToRecycleBin"/>, <see cref="ConflictPolicy.AutoNumber"/> or <see cref="ConflictPolicy.Skip"/>.</param>
/// <param name="ForTheRest">Do the same for the rest of this job without asking again.</param>
public sealed record ConflictAnswer(ConflictPolicy Policy, bool ForTheRest = false);

/// <summary>
/// One job's taken names under the replace and Ask policies. A source of the job, a folder holding one, and
/// an output the job already wrote are never replaced or asked about: those names are numbered instead.
/// Replacing recycles the old item before the new one moves in; undoing the job recycles the new outputs
/// and does not bring the old items back.
/// </summary>
/// <param name="prompt">Asks for <see cref="ConflictPolicy.Ask"/>; without one, a taken name is numbered.</param>
public sealed class JobConflicts(IFileOperations files, IConflictPrompt? prompt, IReadOnlyList<string> sources, CancellationToken cancellationToken)
{
    private readonly List<string> _own = [.. sources.Select(Full)];
    private readonly Lock _gate = new();
    private ConflictPolicy? _forTheRest;
    private int _replaced;

    /// <summary>Old items this job recycled to make room for its outputs.</summary>
    public int Replaced => Volatile.Read(ref _replaced);

    /// <summary>
    /// What to do with <paramref name="target"/>, which exists, under <paramref name="policy"/>:
    /// <see cref="ConflictPolicy.OverwriteToRecycleBin"/>, <see cref="ConflictPolicy.AutoNumber"/> or <see cref="ConflictPolicy.Skip"/>.
    /// </summary>
    /// <param name="more">The job has more items after this one.</param>
    public async Task<ConflictPolicy> SettleAsync(string target, ConflictPolicy policy, bool more)
    {
        if (policy is not (ConflictPolicy.OverwriteToRecycleBin or ConflictPolicy.Ask))
        {
            return policy;
        }

        if (IsOwn(target))
        {
            return ConflictPolicy.AutoNumber;
        }

        if (policy == ConflictPolicy.OverwriteToRecycleBin)
        {
            return policy;
        }

        lock (_gate)
        {
            if (_forTheRest is { } kept)
            {
                return kept;
            }
        }

        if (prompt is null)
        {
            return ConflictPolicy.AutoNumber;
        }

        var answer = await prompt.AskAsync(target, more, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var settled = answer.Policy is ConflictPolicy.OverwriteToRecycleBin or ConflictPolicy.AutoNumber ? answer.Policy : ConflictPolicy.Skip;
        if (answer.ForTheRest)
        {
            lock (_gate)
            {
                _forTheRest = settled;
            }
        }

        return settled;
    }

    /// <summary>
    /// Recycles <paramref name="target"/> to make room. Fails if it is still there afterwards, as when the
    /// user declines to delete for good an item on a drive without a Recycle Bin.
    /// </summary>
    public async Task ReplaceAsync(string target)
    {
        if (IsOwn(target))
        {
            throw new InvalidOperationException($"{target} belongs to this job and is never replaced.");
        }

        await files.RecycleAsync([target], cancellationToken).ConfigureAwait(false);
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException($"{Path.GetFileName(target)} 没能移到回收站，没有替换它");
        }

        Interlocked.Increment(ref _replaced);
    }

    /// <summary>Notes an output this job wrote, so a later item never replaces it.</summary>
    public void Wrote(string path)
    {
        lock (_gate)
        {
            _own.Add(Full(path));
        }
    }

    private bool IsOwn(string target)
    {
        var full = Full(target);
        lock (_gate)
        {
            return _own.Any(own => own.Equals(full, StringComparison.OrdinalIgnoreCase) || IsInside(own, full));
        }
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
