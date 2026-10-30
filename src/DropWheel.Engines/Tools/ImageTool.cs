using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;
using ImageMagick;

namespace DropWheel.Engines.Tools;

/// <summary>
/// A Tools item that writes a changed copy of each image next to it, in the image's own format, named
/// "{name}" plus <see cref="Suffix"/>: the naming template is for conversions and does not apply. Where it
/// goes and what happens on a name clash follow the output settings. A batch is all or nothing, and an
/// image the tool would leave as it was gets no copy.
/// </summary>
public abstract class ImageTool(TimeProvider? time) : IWheelOperation
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public abstract string Id { get; }

    public WheelMode Mode => WheelMode.Tools;

    public abstract string Label { get; }

    public abstract string Hint { get; }

    public abstract string? Glyph { get; }

    /// <summary>The formats it reads and writes back.</summary>
    protected abstract IReadOnlyCollection<string> Formats { get; }

    /// <summary>Added to the source's name; may hold {w} and {h}.</summary>
    protected abstract string Suffix { get; }

    /// <summary>Why an image was left as it was, such as "没有可去除的元数据".</summary>
    protected virtual string Unchanged => L.T("没有变化");

    public bool AppliesTo(MediaKind kind) => kind == MediaKind.Image;

    public Availability Check(ConversionRequest request)
    {
        foreach (var path in request.SourcePaths)
        {
            var format = FormatRegistry.FromPath(path);
            if (format is null || !Formats.Contains(format.Id))
            {
                return Availability.Unavailable(Unsupported(format) ?? L.T("不支持 {0}", format?.DisplayName ?? Path.GetFileName(path)));
            }

            if (!File.Exists(path))
            {
                return Availability.Unavailable(L.T("找不到 {0}", Path.GetFileName(path)));
            }
        }

        return Availability.Available;
    }

    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var now = _time.GetLocalNow();
        var unchanged = 0;
        long before = 0, after = 0;
        try
        {
            var written = await OutputFiles.ForEachSourceAsync(request, progress, (source, i, _) => Task.Run(() =>
            {
                var formatId = FormatRegistry.FromPath(source)!.Id;
                Rendered? rendered;
                try
                {
                    rendered = Render(source, formatId);
                }
                catch (Exception ex) when (ex is MagickException or InvalidDataException)
                {
                    ex.Data["source"] = source;
                    throw;
                }

                if (rendered is null)
                {
                    unchanged++;
                    return null;
                }

                before += new FileInfo(source).Length;
                after += rendered.Data.Length;
                var plan = request with { TargetFormatId = formatId, Naming = NamingTemplate.Default + Suffix };
                return OutputFiles.Write(plan, source, i, now, temp => File.WriteAllBytes(temp, rendered.Data), rendered.Width, rendered.Height, cancellationToken: cancellationToken);
            }, cancellationToken), cancellationToken).ConfigureAwait(false);

            if (unchanged == request.SourcePaths.Count)
            {
                return new(true, []) { Skipped = true, Detail = Unchanged };
            }

            var notes = new[] { Detail(before, after), unchanged > 0 ? L.T("另有 {0} 张{1}", unchanged, Unchanged) : null };
            return ConversionResult.Written(written) with { Detail = string.Join(L.T("；"), notes.OfType<string>()) is { Length: > 0 } d ? d : null };
        }
        catch (Exception ex) when (ex is MagickException or InvalidDataException)
        {
            return new(false, [], L.T("{0} 无法处理：{1}", Path.GetFileName(ex.Data["source"] as string), ex.Message));
        }
    }

    /// <summary>The new file, or null when it would be no different from the source.</summary>
    protected abstract Rendered? Render(string source, string formatId);

    /// <summary>
    /// Why a format is not covered, for a reason more useful than "不支持": by default, for an image the wheel
    /// converts but no tool writes back, what to convert it to first.
    /// </summary>
    protected virtual string? Unsupported(FileFormat? format) =>
        format is not null && Image.MagickImageConverter.ConvertsOnly(format.Id) ? L.T("{0}不改 {1} 文件，先转成 PNG 或 JPG", Label, format.DisplayName) : null;

    /// <summary>One line for the task window about what was written.</summary>
    protected virtual string? Detail(long before, long after) => null;

    /// <summary>Every frame of an animation and every page of a TIFF, an animation's frames made whole so each can be changed alone.</summary>
    protected static MagickImageCollection Frames(string source)
    {
        var frames = new MagickImageCollection(source);
        if (frames.Count > 1 && frames[0].Format is MagickFormat.Gif or MagickFormat.WebP)
        {
            frames.Coalesce();
        }

        return frames;
    }

    protected static MagickFormat MagickFormatOf(string formatId) => Image.MagickImageConverter.Formats[formatId];

    protected sealed record Rendered(byte[] Data, int? Width = null, int? Height = null);
}
