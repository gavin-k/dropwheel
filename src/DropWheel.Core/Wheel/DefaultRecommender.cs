using DropWheel.Core.Conversion;

namespace DropWheel.Core.Wheel;

/// <summary>
/// M0 rules: a fixed preference list per media kind, minus the source format and anything no engine
/// can produce from it. Other reachable targets follow in alphabetical order.
/// </summary>
public sealed class DefaultRecommender(ConversionGraph graph) : IRecommender
{
    private static readonly Dictionary<MediaKind, string[]> Preferred = new()
    {
        [MediaKind.Image] = ["png", "jpg", "webp", "avif", "gif", "tiff", "bmp"],
        [MediaKind.Subtitle] = ["srt", "vtt", "ass", "lrc", "ssa"],
    };

    public IEnumerable<string> For(IReadOnlyList<string> sourcePaths, MediaKind kind, WheelMode mode)
    {
        if (mode != WheelMode.Convert || sourcePaths.Count == 0 || FormatRegistry.FromPath(sourcePaths[0]) is not { } from)
        {
            return [];
        }

        var reachable = graph.TargetsFrom(from.Id).Where(id => id != from.Id).ToHashSet();
        var preferred = Preferred.GetValueOrDefault(kind, []).Where(reachable.Contains);
        return preferred.Concat(reachable.Order(StringComparer.Ordinal)).Distinct();
    }
}
