namespace DropWheel.Core.Conversion;

/// <summary>Coarse file category used for wheel bucketing and engine routing.</summary>
public enum MediaKind
{
    Unknown = 0,
    Image,
    Video,
    Audio,
    Pdf,
    Document,
    Slides,
    Sheet,
    Subtitle,
    Archive,
}
