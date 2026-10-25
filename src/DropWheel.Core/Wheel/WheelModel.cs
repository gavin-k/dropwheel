using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;

namespace DropWheel.Core.Wheel;

public enum WheelMode { Convert, Tools, Actions }

public enum SlotSource { Recent, Recommended }

/// <summary>One of the six sectors.</summary>
public sealed record WheelSlot(
    int Index,
    string ItemId,
    string Label,
    string SubLabel,
    SlotSource Source,
    Availability Availability,
    QualityEstimate? Quality);

public sealed record WheelModel(WheelMode Mode, string CenterTitle, string CenterSubtitle, IReadOnlyList<WheelSlot> Slots)
{
    public const int SlotCount = 6;
}

/// <summary>Builds the 6-slot wheel: 3 most-recent + 3 recommended, deduplicated, unavailable kept but greyed.</summary>
public sealed class WheelBuilder(ConversionGraph graph, IRecentItems recent, IRecommender recommender)
{
    public WheelModel Build(IReadOnlyList<string> sourcePaths, WheelMode mode, AppSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sourcePaths.Count);
        var format = FormatRegistry.FromPath(sourcePaths[0]);
        var kind = format?.Kind ?? MediaKind.Unknown;

        var picked = new List<(string id, SlotSource src)>();
        foreach (var id in recent.For(kind, mode).Take(3))
        {
            picked.Add((id, SlotSource.Recent));
        }

        foreach (var id in recommender.For(sourcePaths, kind, mode))
        {
            if (picked.Count >= WheelModel.SlotCount)
            {
                break;
            }

            if (picked.All(p => p.id != id))
            {
                picked.Add((id, SlotSource.Recommended));
            }
        }

        var slots = picked.Select((p, i) =>
        {
            var request = settings.Request(sourcePaths, p.id);
            var availability = mode == WheelMode.Convert ? graph.Check(request) : Availability.Available;
            var quality = mode == WheelMode.Convert && availability.IsAvailable && format is not null
                ? graph.Resolve(format.Id, p.id)?.Estimate(request)
                : null;
            var target = FormatRegistry.FromExtension(p.id);
            return new WheelSlot(
                i,
                p.id,
                target?.DisplayName ?? p.id.ToUpperInvariant(),
                availability.IsAvailable ? (p.src == SlotSource.Recent ? "最近" : "推荐") : "不可用",
                p.src,
                availability,
                quality);
        }).ToList();

        var name = Path.GetFileName(sourcePaths[0]);
        var subtitle = sourcePaths.Count > 1 ? $"{sourcePaths.Count} 个文件" : (format?.DisplayName ?? "未知类型");
        return new WheelModel(mode, name, subtitle, slots);
    }
}

public interface IRecentItems
{
    IEnumerable<string> For(MediaKind kind, WheelMode mode);
    void Touch(MediaKind kind, WheelMode mode, string itemId);
}

public interface IRecommender
{
    IEnumerable<string> For(IReadOnlyList<string> sourcePaths, MediaKind kind, WheelMode mode);
}
