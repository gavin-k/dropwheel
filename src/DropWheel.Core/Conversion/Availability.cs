namespace DropWheel.Core.Conversion;

/// <summary>
/// Whether a wheel item can be used right now. An unavailable item is still shown (greyed)
/// together with its <see cref="Reason"/> — never silently hidden.
/// </summary>
public sealed record Availability(bool IsAvailable, string? Reason = null)
{
    public static Availability Available { get; } = new(true);

    public static Availability Unavailable(string reason) => new(false, reason);

    /// <summary>Reasons are grouped so settings can deep-link to the fix.</summary>
    public UnavailableCause Cause { get; init; } = UnavailableCause.None;
}

public enum UnavailableCause
{
    None = 0,
    /// <summary>Format can only be read, never written (HEIC, RAW, RAR, DOC…).</summary>
    InputOnly,
    /// <summary>A system codec is missing (none yet; HEVC Video Extensions once HEIC is written through WIC).</summary>
    MissingSystemCodec,
    /// <summary>An optional engine module (LibreOffice, RapidOCR model) is not installed.</summary>
    MissingOptionalModule,
    /// <summary>Operation needs more / fewer files than selected (merge PDF needs ≥ 2).</summary>
    SelectionShape,
    /// <summary>Action is turned off in Settings › File actions.</summary>
    DisabledInSettings,
    /// <summary>Daily free quota exhausted.</summary>
    QuotaExhausted,
    /// <summary>Source analysis says the result would be unreliable (PDF → XLSX with no tables).</summary>
    UnreliableResult,
}
