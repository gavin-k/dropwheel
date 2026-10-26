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
    IWheelOperation? Operation = null)
{
    /// <summary>
    /// What the hub says while the pointer is on this sector, which has no room for it: why the item is off,
    /// otherwise the expected quality and its first reason; null when there is nothing to add.
    /// </summary>
    public SlotDetail? Detail => !Availability.IsAvailable
        ? new($"{Label} {SubLabel}", Availability.Reason ?? "")
        : Quality is { } q ? new(L.T("预期质量{0}", LevelName(q.Level)), q.Reasons.Count > 0 ? q.Reasons[0] : "") : null;

    /// <summary>"PNG，推荐，预期质量高：…；…" or "HEIC，匹配，不可用：…": the sector's name for screen readers, every reason included.</summary>
    public string AccessibleName
    {
        get
        {
            var said = Availability.IsAvailable ? SubLabel : SourceName(Source);
            List<string> parts = [Label];
            if (said.Length > 0)
            {
                parts.Add(said);
            }

            if (!Availability.IsAvailable)
            {
                parts.Add(Availability.Reason is { } reason ? L.T("{0}：{1}", SubLabel, reason) : SubLabel);
            }
            else if (Quality is { } q)
            {
                parts.Add(q.Reasons.Count > 0 ? L.T("预期质量{0}：{1}", LevelName(q.Level), string.Join(L.T("；"), q.Reasons)) : L.T("预期质量{0}", LevelName(q.Level)));
            }

            return string.Join(L.T("，"), parts);
        }
    }

    /// <summary>Why a Convert item is on the wheel; empty for a Tools or Actions item, which is always there.</summary>
    public static string SourceName(SlotSource source) => source switch
    {
        SlotSource.Recent => L.T("最近"),
        SlotSource.Recommended => L.T("推荐"),
        SlotSource.Search => L.T("匹配"),
        _ => "",
    };

    private static string LevelName(QualityLevel level) => level switch
    {
        QualityLevel.High => L.T("高"),
        QualityLevel.Medium => L.T("中"),
        _ => L.T("低"),
    };
}

/// <param name="Head">One short line in place of the hub's title.</param>
/// <param name="Body">The reason, in place of the subtitle; it may wrap.</param>
public sealed record SlotDetail(string Head, string Body);

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
                availability.IsAvailable ? WheelSlot.SourceName(p.src) : L.T("不可用"),
                p.src,
                availability,
                quality);
        }).ToList();

        var hasMore = matches.Count > WheelModel.SlotCount;
        if (query.Length > 0)
        {
            return new WheelModel(mode, L.T("搜索 {0}", query.ToUpperInvariant()), SearchSubtitle(format, query, matches.Count), slots, hasMore);
        }

        var name = Path.GetFileName(sourcePaths[0]);
        var subtitle = sourcePaths.Count > 1 ? L.T("{0} 个文件", sourcePaths.Count) : (format?.DisplayName ?? L.T("未知类型"));
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
            return L.T("{0} 个格式", count);
        }

        return FormatRegistry.FromExtension(query) switch
        {
            { } named when named.Id == source?.Id => L.T("已经是 {0}", named.DisplayName),
            { } named => L.T("转不了 {0}", named.DisplayName),
            null => L.T("没有这个格式"),
        };
    }

    private WheelModel BuildOperations(IReadOnlyList<string> sourcePaths, WheelMode mode, MediaKind kind, AppSettings settings)
    {
        var slots = (operations ?? []).Where(o => o.Mode == mode && o.AppliesTo(kind)).Take(WheelModel.SlotCount).Select((op, i) =>
        {
            var availability = op.Action is { } action && !settings.Actions.IsEnabled(action)
                ? Availability.Unavailable(FileActionSettings.DisabledReason) with { Cause = UnavailableCause.DisabledInSettings }
                : op.Check(settings.Request(sourcePaths, op.Id));
            var subLabel = availability.IsAvailable ? op.Hint : availability.Cause == UnavailableCause.DisabledInSettings ? L.T("未开启") : L.T("不可用");
            return new WheelSlot(i, op.Id, op.Label, subLabel, SlotSource.Fixed, availability, null, op);
        }).ToList();

        var title = mode == WheelMode.Tools ? L.T("工具") : L.T("动作");
        var subtitle = slots.Count == 0 ? (mode == WheelMode.Tools ? L.T("这类文件还没有工具") : L.T("这类文件还没有动作"))
            : sourcePaths.Count > 1 ? L.T("{0} · {1} 项", title, sourcePaths.Count) : title;
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
