using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;

namespace DropWheel.Core.Wheel;

public enum WheelMode { Convert, Tools, Actions }

public enum SlotSource { Recent, Recommended, Fixed, Search }

/// <summary>One of the six sectors.</summary>
/// <param name="Operation">What runs a Tools or Actions item; null for a Convert item, which the graph resolves.</param>
public sealed record WheelSlot(
    int Index,
    string ItemId,
    string Label,
    string SubLabel,
    SlotSource Source,
    Availability Availability,
    QualityEstimate? Quality,
    IWheelOperation? Operation = null);

/// <param name="HasMore">More items than the slots show: the Convert wheel's other targets, or more matches for a search.</param>
public sealed record WheelModel(WheelMode Mode, string CenterTitle, string CenterSubtitle, IReadOnlyList<WheelSlot> Slots, bool HasMore = false)
{
    public const int SlotCount = 6;

    /// <summary>Longest search the Convert wheel takes; no format id comes close.</summary>
    public const int MaxQuery = 8;
}

/// <summary>
/// Builds the 6-slot wheel. Convert: 3 most-recent + 3 recommended, deduplicated; with a search, the
/// targets that match it, best match first. Tools and Actions: the operations for that mode and kind in
/// registration order, so each keeps its place. Unavailable items are kept but greyed.
/// </summary>
public sealed class WheelBuilder(ConversionGraph graph, IRecentItems recent, IRecommender recommender, IReadOnlyList<IWheelOperation>? operations = null)
{
    /// <param name="query">Letters and digits typed on the Convert wheel; empty for none. Other wheels ignore it.</param>
    public WheelModel Build(IReadOnlyList<string> sourcePaths, WheelMode mode, AppSettings settings, string query = "")
    {
        ArgumentOutOfRangeException.ThrowIfZero(sourcePaths.Count);
        var format = FormatRegistry.FromPath(sourcePaths[0]);
        var kind = format?.Kind ?? MediaKind.Unknown;
        if (mode != WheelMode.Convert)
        {
            return BuildOperations(sourcePaths, mode, kind, settings);
        }

        // Every target in the order the wheel would offer it: recent, then recommended, then the rest.
        var candidates = new List<(string id, SlotSource src)>();
        foreach (var id in recent.For(kind, mode).Take(3))
        {
            candidates.Add((id, SlotSource.Recent));
        }

        var others = format is null ? [] : graph.TargetsFrom(format.Id).Where(id => id != format.Id);
        foreach (var id in recommender.For(sourcePaths, kind, mode).Concat(others))
        {
            if (candidates.All(p => p.id != id))
            {
                candidates.Add((id, SlotSource.Recommended));
            }
        }

        var matches = query.Length == 0
            ? candidates
            : [.. candidates.Select(c => (c, rank: Rank(c.id, query))).Where(m => m.rank >= 0).OrderBy(m => m.rank).Select(m => (m.c.id, SlotSource.Search))];
        var picked = matches.Take(WheelModel.SlotCount).ToList();

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
                !availability.IsAvailable ? "不可用" : p.src switch { SlotSource.Recent => "最近", SlotSource.Search => "匹配", _ => "推荐" },
                p.src,
                availability,
                quality);
        }).ToList();

        var hasMore = matches.Count > WheelModel.SlotCount;
        if (query.Length > 0)
        {
            return new WheelModel(mode, $"搜索 {query.ToUpperInvariant()}", SearchSubtitle(format, query, matches.Count), slots, hasMore);
        }

        var name = Path.GetFileName(sourcePaths[0]);
        var subtitle = sourcePaths.Count > 1 ? $"{sourcePaths.Count} 个文件" : (format?.DisplayName ?? "未知类型");
        return new WheelModel(mode, name, subtitle, slots, hasMore);
    }

    /// <summary>
    /// How well a target matches a search, lower is better, or -1: the extension or an alias is the search,
    /// then starts with it, then the display name without punctuation starts with it, then the extension
    /// contains it.
    /// </summary>
    private static int Rank(string id, string query)
    {
        var format = FormatRegistry.FromExtension(id);
        string[] names = [id, .. format?.Aliases ?? []];
        if (names.Any(n => n.Equals(query, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        if (names.Any(n => n.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        var display = new string([.. (format?.DisplayName ?? "").Where(char.IsLetterOrDigit)]);
        if (display.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return id.Contains(query, StringComparison.OrdinalIgnoreCase) ? 3 : -1;
    }

    /// <summary>The hub's subtitle for a search: how many matched, or why the format it names is not there.</summary>
    private static string SearchSubtitle(FileFormat? source, string query, int count)
    {
        if (count > 0)
        {
            return $"{count} 个格式";
        }

        return FormatRegistry.FromExtension(query) switch
        {
            { } named when named.Id == source?.Id => $"已经是 {named.DisplayName}",
            { } named => $"转不了 {named.DisplayName}",
            null => "没有这个格式",
        };
    }

    private WheelModel BuildOperations(IReadOnlyList<string> sourcePaths, WheelMode mode, MediaKind kind, AppSettings settings)
    {
        var slots = (operations ?? []).Where(o => o.Mode == mode && o.AppliesTo(kind)).Take(WheelModel.SlotCount).Select((op, i) =>
        {
            var availability = op.Action is { } action && !settings.Actions.IsEnabled(action)
                ? Availability.Unavailable(FileActionSettings.DisabledReason) with { Cause = UnavailableCause.DisabledInSettings }
                : op.Check(settings.Request(sourcePaths, op.Id));
            var subLabel = availability.IsAvailable ? op.Hint : availability.Cause == UnavailableCause.DisabledInSettings ? "未开启" : "不可用";
            return new WheelSlot(i, op.Id, op.Label, subLabel, SlotSource.Fixed, availability, null, op);
        }).ToList();

        var title = mode == WheelMode.Tools ? "工具" : "动作";
        var subtitle = slots.Count == 0 ? $"这类文件还没有{title}" : sourcePaths.Count > 1 ? $"{title} · {sourcePaths.Count} 项" : title;
        return new WheelModel(mode, Path.GetFileName(sourcePaths[0]), subtitle, slots);
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
