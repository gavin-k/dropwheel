using DropWheel.Core.Conversion;

namespace DropWheel.Core.Jobs;

public enum JobState { Pending, Running, Done, Failed, Canceled, Undone, Skipped }

/// <summary>How to reverse a finished job. Never touches the source file.</summary>
public abstract record UndoPlan
{
    /// <summary>Move produced outputs to the Recycle Bin.</summary>
    public sealed record RecycleOutputs(IReadOnlyList<string> Paths) : UndoPlan;

    /// <summary>A "move" file action: put the file back where it came from.</summary>
    public sealed record MoveBack(string From, string To) : UndoPlan;

    /// <summary>"Copy path" action: restore previous clipboard text.</summary>
    public sealed record RestoreClipboard(string? PreviousText) : UndoPlan;

    public sealed record None : UndoPlan;
}

public sealed class Job
{
    public Guid Id { get; } = Guid.NewGuid();
    public required ConversionRequest Request { get; init; }
    public required string Label { get; init; }
    public QualityEstimate? Quality { get; init; }
    public bool CountsTowardQuota { get; init; } = true;

    public JobState State { get; internal set; } = JobState.Pending;
    public double Progress { get; internal set; }
    public string? Detail { get; internal set; }
    public string? Error { get; internal set; }
    public IReadOnlyList<string> Outputs { get; internal set; } = [];
    public UndoPlan Undo { get; internal set; } = new UndoPlan.None();
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; internal set; }
}
