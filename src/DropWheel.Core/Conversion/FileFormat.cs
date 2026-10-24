namespace DropWheel.Core.Conversion;

/// <summary>
/// A file format known to DropWheel. <see cref="Id"/> is the canonical lower-case extension
/// without the dot ("png", "docx"); <see cref="Aliases"/> lists other extensions that map to it.
/// </summary>
/// <param name="Id">Canonical extension, lower-case, no dot.</param>
/// <param name="DisplayName">Label shown in the wheel ("PNG", "PDF/A").</param>
/// <param name="Kind">Media category.</param>
/// <param name="CanOutput">False for input-only formats (RAW, RAR, DOC…).</param>
/// <param name="InputOnlyReason">User-facing reason when <see cref="CanOutput"/> is false.</param>
/// <param name="Aliases">Other extensions that resolve to this format.</param>
public sealed record FileFormat(
    string Id,
    string DisplayName,
    MediaKind Kind,
    bool CanOutput = true,
    string? InputOnlyReason = null,
    params string[] Aliases)
{
    public bool Matches(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return ext == Id || Aliases.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }
}
