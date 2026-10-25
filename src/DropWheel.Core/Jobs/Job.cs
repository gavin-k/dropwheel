using DropWheel.Core.Conversion;

namespace DropWheel.Core.Jobs;

public enum JobState { Pending, Running, Done, Failed, Canceled, Undone, Skipped }

/// <summary>How to reverse a finished job. Never touches the source file.</summary>
public abstract record UndoPlan
{
    /// <summary>Move produced outputs to the Recycle Bin.</summary>
    public sealed record RecycleOutputs(IReadOnlyList<string> Paths) : UndoPlan;

    /// <summary>A "move to" file action: <see cref="Moves"/> are the moves it made; undo makes them in reverse.</summary>
    public sealed record MoveBack(IReadOnlyList<FileMove> Moves) : UndoPlan;

    public sealed record None : UndoPlan;
}

/// <summary>One item moved or copied from <paramref name="From"/> to <paramref name="To"/>; both are full paths.</summary>
public sealed record FileMove(string From, string To);

public sealed class Job
{
    public Guid Id { get; } = Guid.NewGuid();
    public required ConversionRequest Request { get; init; }
    public required string Label { get; init; }

    /// <summary>What runs the request; null means the converter the graph resolves for its formats.</summary>
    public IJobRunner? Runner { get; init; }
    public bool CountsTowardQuota { get; init; } = true;

    public JobState State { get; internal set; } = JobState.Pending;
    public double Progress { get; internal set; }
    public string? Detail { get; internal set; }
    public string? Error { get; internal set; }
    public IReadOnlyList<string> Outputs { get; internal set; } = [];
    public UndoPlan Undo { get; internal set; } = new UndoPlan.None();

    /// <summary>Items of the same name the job recycled to put its outputs in their place. Undo does not bring them back.</summary>
    public int Replaced { get; internal set; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; internal set; }
}
