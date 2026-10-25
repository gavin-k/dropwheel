using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;

namespace DropWheel.Engines.Subtitle;

/// <summary>
/// Text subtitles between SRT, WebVTT, ASS, SSA and LRC, in process. Timing and the line text survive;
/// styling is cut down to italic, bold and underline. Any input encoding, UTF-8 output, see <see cref="SubtitleText"/>.
/// </summary>
public sealed class SubtitleConverter(TimeProvider? time = null) : IConverter
{
    private static readonly string[] Formats = ["srt", "vtt", "ass", "ssa", "lrc"];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "subtitle";

    public IEnumerable<ConversionEdge> Edges =>
        from i in Formats
        from o in Formats
        where i != o
        select new ConversionEdge(i, o);

    public Availability Check(ConversionRequest request) => Availability.Available;

    public QualityEstimate? Estimate(ConversionRequest request)
    {
        var from = FormatRegistry.FromPath(request.SourcePaths[0])?.Id;
        var to = request.TargetFormatId;
        var reasons = new List<string>();
        if (from is "ass" or "ssa")
        {
            reasons.Add("样式、位置和特效会丢失");
        }

        if (from == "lrc")
        {
            reasons.Add("LRC 没有结束时间，按下一行的开始时间推算");
        }

        if (to == "lrc")
        {
            reasons.Add("多行字幕会合并成一行，粗斜体丢失");
        }

        return reasons.Count > 0 ? new(QualityLevel.Medium, reasons) : null;
    }

    /// <summary>All or nothing, see <see cref="OutputFiles.ForEachSourceAsync"/>.</summary>
    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Formats.Contains(request.TargetFormatId))
        {
            return new(false, [], $"不支持输出 {request.TargetFormatId}");
        }

        try
        {
            var written = await OutputFiles.ForEachSourceAsync(request, progress,
                (source, i, _) => Convert(request, source, i, cancellationToken), cancellationToken).ConfigureAwait(false);
            return new(true, written);
        }
        catch (InvalidDataException ex)
        {
            return new(false, [], ex.Message);
        }
    }

    private async Task<string?> Convert(ConversionRequest request, string source, int index, CancellationToken cancellationToken)
    {
        var from = FormatRegistry.FromPath(source)?.Id;
        if (from is null || !Formats.Contains(from))
        {
            throw new InvalidDataException($"{Path.GetFileName(source)} 不是可转换的文本字幕");
        }

        var text = SubtitleText.Decode(await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false));
        var cues = SubtitleFormats.Parse(from, text);
        if (cues.Count == 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(source)} 里没有找到任何带时间的字幕");
        }

        var output = SubtitleFormats.Write(request.TargetFormatId, cues);
        return await OutputFiles.WriteAsync(request, source, index, _time.GetLocalNow(),
            temp => File.WriteAllTextAsync(temp, output, SubtitleText.Output, cancellationToken)).ConfigureAwait(false);
    }
}
