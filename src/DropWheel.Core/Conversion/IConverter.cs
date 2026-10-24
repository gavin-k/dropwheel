namespace DropWheel.Core.Conversion;

/// <summary>One directed edge an engine supports.</summary>
public sealed record ConversionEdge(string FromFormatId, string ToFormatId);

public sealed record ConversionRequest(
    IReadOnlyList<string> SourcePaths,
    string TargetFormatId,
    string OutputDirectory,
    IReadOnlyDictionary<string, string>? Options = null);

public sealed record ConversionResult(
    bool Succeeded,
    IReadOnlyList<string> OutputPaths,
    string? Error = null);

public enum QualityLevel { Low = 1, Medium = 2, High = 3 }

/// <summary>Shown in the wheel before the user releases the mouse.</summary>
public sealed record QualityEstimate(QualityLevel Level, IReadOnlyList<string> Reasons);

/// <summary>
/// Contract every engine adapter implements. Engines are discovered at startup and registered
/// into the <see cref="ConversionGraph"/>. Heavy engines run as sidecar processes behind this
/// interface; the UI never knows the difference.
/// </summary>
public interface IConverter
{
    /// <summary>Stable id, e.g. "magick", "ffmpeg", "pdfium".</summary>
    string Id { get; }

    IEnumerable<ConversionEdge> Edges { get; }

    /// <summary>Cheap, synchronous. Called for every wheel slot on open.</summary>
    Availability Check(ConversionRequest request);

    /// <summary>Optional. Only complex conversions (PDF → DOCX…) return a value.</summary>
    QualityEstimate? Estimate(ConversionRequest request) => null;

    Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken);
}
