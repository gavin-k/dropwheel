namespace DropWheel.Core.Conversion;

/// <summary>
/// Static catalogue of formats. Engines declare which edges between these formats they support;
/// the registry only knows names, kinds and input-only reasons.
/// </summary>
public static class FormatRegistry
{
    public static IReadOnlyList<FileFormat> All { get; } =
    [
        // Image
        new("jpg", "JPG", MediaKind.Image, Aliases: ["jpeg", "jpe"]),
        new("png", "PNG", MediaKind.Image),
        new("webp", "WEBP", MediaKind.Image),
        new("avif", "AVIF", MediaKind.Image),
        new("heic", "HEIC", MediaKind.Image, Aliases: ["heif"]),
        new("gif", "GIF", MediaKind.Image),
        new("bmp", "BMP", MediaKind.Image),
        new("tiff", "TIFF", MediaKind.Image, Aliases: ["tif"]),
        new("ico", "ICO", MediaKind.Image),
        new("svg", "SVG", MediaKind.Image, CanOutput: false, InputOnlyReason: "位图转矢量需要描摹，结果不可控；只读取并栅格化。"),
        new("psd", "PSD", MediaKind.Image, CanOutput: false, InputOnlyReason: "读取合成后的图像；写回会丢失图层。"),
        new("raw", "RAW", MediaKind.Image, CanOutput: false, InputOnlyReason: "相机私有格式，没有公开的编码器。", Aliases: ["cr2", "cr3", "nef", "arw", "dng", "orf", "rw2"]),

        // Video
        new("mp4", "MP4", MediaKind.Video, Aliases: ["m4v"]),
        new("mov", "MOV", MediaKind.Video),
        new("mkv", "MKV", MediaKind.Video),
        new("webm", "WEBM", MediaKind.Video),
        new("avi", "AVI", MediaKind.Video),
        new("rmvb", "RMVB", MediaKind.Video, CanOutput: false, InputOnlyReason: "RealMedia 编码器不再公开授权，仅能解码。", Aliases: ["rm"]),

        // Audio
        new("mp3", "MP3", MediaKind.Audio),
        new("aac", "AAC", MediaKind.Audio),
        new("m4a", "M4A", MediaKind.Audio),
        new("wav", "WAV", MediaKind.Audio),
        new("flac", "FLAC", MediaKind.Audio),
        new("ogg", "OGG", MediaKind.Audio),
        new("opus", "OPUS", MediaKind.Audio),
        new("aiff", "AIFF", MediaKind.Audio, Aliases: ["aif"]),
        new("amr", "AMR", MediaKind.Audio, CanOutput: false, InputOnlyReason: "语音编码器需专利授权，仅能解码。"),

        // PDF
        new("pdf", "PDF", MediaKind.Pdf),
        new("pdfa", "PDF/A", MediaKind.Pdf),
        new("xps", "XPS", MediaKind.Pdf, CanOutput: false, InputOnlyReason: "仅读取并转为 PDF，不再生成 XPS。", Aliases: ["oxps"]),

        // Document
        new("docx", "DOCX", MediaKind.Document),
        new("odt", "ODT", MediaKind.Document),
        new("rtf", "RTF", MediaKind.Document),
        new("txt", "TXT", MediaKind.Document),
        new("md", "MD", MediaKind.Document, Aliases: ["markdown"]),
        new("html", "HTML", MediaKind.Document, Aliases: ["htm"]),
        new("epub", "EPUB", MediaKind.Document),
        new("doc", "DOC", MediaKind.Document, CanOutput: false, InputOnlyReason: "旧版二进制格式，仅读取；建议输出 DOCX。"),
        new("pages", "PAGES", MediaKind.Document, CanOutput: false, InputOnlyReason: "Apple 私有格式，仅读取内嵌预览。"),

        // Slides
        new("pptx", "PPTX", MediaKind.Slides),
        new("odp", "ODP", MediaKind.Slides),
        new("ppt", "PPT", MediaKind.Slides, CanOutput: false, InputOnlyReason: "旧版二进制格式，仅读取。"),
        new("key", "KEY", MediaKind.Slides, CanOutput: false, InputOnlyReason: "Apple 私有格式，仅读取预览。"),

        // Sheet
        new("xlsx", "XLSX", MediaKind.Sheet),
        new("ods", "ODS", MediaKind.Sheet),
        new("csv", "CSV", MediaKind.Sheet),
        new("tsv", "TSV", MediaKind.Sheet),
        new("xls", "XLS", MediaKind.Sheet, CanOutput: false, InputOnlyReason: "旧版二进制格式，仅读取。"),
        new("numbers", "NUMBERS", MediaKind.Sheet, CanOutput: false, InputOnlyReason: "Apple 私有格式，仅读取。"),

        // Subtitle
        new("srt", "SRT", MediaKind.Subtitle),
        new("vtt", "VTT", MediaKind.Subtitle),
        new("ass", "ASS", MediaKind.Subtitle),
        new("ssa", "SSA", MediaKind.Subtitle),
        new("lrc", "LRC", MediaKind.Subtitle),
        new("sub", "SUB/IDX", MediaKind.Subtitle, CanOutput: false, InputOnlyReason: "图形字幕需要 OCR，目前仅提取图像。", Aliases: ["idx"]),

        // Archive
        new("zip", "ZIP", MediaKind.Archive),
        new("7z", "7Z", MediaKind.Archive),
        new("tar", "TAR", MediaKind.Archive),
        new("tgz", "TAR.GZ", MediaKind.Archive, Aliases: ["gz"]),
        new("rar", "RAR", MediaKind.Archive, CanOutput: false, InputOnlyReason: "压缩算法受授权限制，只能解压。"),
    ];

    public static FileFormat? FromExtension(string extension)
        => All.FirstOrDefault(f => f.Matches(extension));

    public static FileFormat? FromPath(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return FromExtension("tgz");
        }

        return FromExtension(Path.GetExtension(path));
    }

    /// <summary>The file name without its format extension, so "backup.tar.gz" gives "backup", not "backup.tar".</summary>
    public static string StemOf(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? name[..^".tar.gz".Length] : Path.GetFileNameWithoutExtension(name);
    }

    public static IEnumerable<FileFormat> OfKind(MediaKind kind) => All.Where(f => f.Kind == kind);
}
