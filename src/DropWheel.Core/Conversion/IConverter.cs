using DropWheel.Core.Jobs;

namespace DropWheel.Core.Conversion;

/// <summary>One directed edge an engine supports.</summary>
public sealed record ConversionEdge(string FromFormatId, string ToFormatId);

/// <param name="OutputDirectory">Where outputs go; empty means next to each source file.</param>
public sealed record ConversionRequest(
    IReadOnlyList<string> SourcePaths,
    string TargetFormatId,
    string OutputDirectory,
    IReadOnlyDictionary<string, string>? Options = null)
{
    /// <summary>Output name template, see <see cref="NamingTemplate"/>.</summary>
    public string Naming { get; init; } = NamingTemplate.Default;

    public ConflictPolicy Conflict { get; init; } = ConflictPolicy.AutoNumber;

    /// <summary>
    /// Settles taken names under the replace and Ask policies. The job queue sets it for each run; a request
    /// derived with <c>with</c> keeps it, so derive rather than build a new one.
    /// </summary>
    public JobConflicts? Conflicts { get; init; }
}

/// <param name="OutputPaths">What the job produced. The task window opens their folder, and undo recycles them unless <see cref="Undo"/> says otherwise.</param>
public sealed record ConversionResult(
    bool Succeeded,
    IReadOnlyList<string> OutputPaths,
    string? Error = null)
{
    /// <summary>Nothing was written because the conflict policy skipped every source.</summary>
    public bool Skipped { get; init; }

    /// <summary>How to reverse the job; null means recycle <see cref="OutputPaths"/>.</summary>
    public UndoPlan? Undo { get; init; }

    /// <summary>One line for the task window, such as "已移到回收站".</summary>
    public string? Detail { get; init; }

    /// <summary>An engine's result: the outputs it wrote, skipped when the conflict policy left nothing to write.</summary>
    public static ConversionResult Written(IReadOnlyList<string> outputs) => new(true, outputs) { Skipped = outputs.Count == 0 };
}

public enum QualityLevel { Low = 1, Medium = 2, High = 3 }

/// <summary>Shown in the wheel before the user releases the mouse.</summary>
public sealed record QualityEstimate(QualityLevel Level, IReadOnlyList<string> Reasons);

/// <summary>Whatever a job runs: a converter, a tool or a file action.</summary>
public interface IJobRunner
{
    /// <summary>Cheap, synchronous. Called for every wheel slot on open.</summary>
    Availability Check(ConversionRequest request);

    Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Contract every engine adapter implements. Engines are discovered at startup and registered
/// into the <see cref="ConversionGraph"/>. Heavy engines run as sidecar processes behind this
/// interface; the UI never knows the difference.
/// </summary>
public interface IConverter : IJobRunner
{
    /// <summary>Stable id, e.g. "magick", "ffmpeg", "pdfium".</summary>
    string Id { get; }

    IEnumerable<ConversionEdge> Edges { get; }

    /// <summary>Optional. Only complex conversions (PDF → DOCX…) return a value.</summary>
    QualityEstimate? Estimate(ConversionRequest request) => null;
}
