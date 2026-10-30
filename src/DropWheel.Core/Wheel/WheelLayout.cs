using System.Text.Json.Serialization;
using DropWheel.Core.Conversion;
using DropWheel.Core.Tools;

namespace DropWheel.Core.Wheel;

/// <summary>A sector that opens as an outer ring of up to six items.</summary>
public sealed record LayoutGroup(LocalizedText Name, IReadOnlyList<string> Items)
{
    public const int Capacity = WheelModel.SlotCount;

    /// <summary>The name a group gets when a full wheel folds a sector to make room and the manifest named none.</summary>
    public static LocalizedText DefaultName { get; } = new("更多", "More");

    public bool IsFull => Items.Count >= Capacity;

    public LayoutGroup Add(string id) => this with { Items = [.. Items, id] };
}

/// <summary>One sector of a laid-out wheel: an item, or a group. A null in the slot list is an empty sector.</summary>
public sealed record LayoutSlot(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Item = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutGroup? Group = null)
{
    public bool Holds(string id) => Item == id || Group?.Items.Contains(id) == true;
}

/// <summary>
/// layout.json: where each item sits on the Tools wheel for each kind of file and on the Actions wheel, and
/// which user tools have been placed once from their manifest's hint. A wheel without an entry keeps the
/// registration order; one with an entry shows what the entry says and nothing else. Keys are "tools" or
/// "actions", then the kind of file in lower case, or "any" for the Actions wheel, which is the same for every kind.
/// </summary>
public sealed record WheelLayout(int Version, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<LayoutSlot?>>> Wheels, IReadOnlyList<string> Installed)
{
    public const int CurrentVersion = 1;

    public const string AnyKind = "any";

    public static WheelLayout Empty { get; } = new(CurrentVersion, new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<LayoutSlot?>>>(), []);

    public static string WheelKey(WheelMode mode) => mode == WheelMode.Actions ? "actions" : "tools";

    public static string KindKey(WheelMode mode, MediaKind kind) => mode == WheelMode.Actions ? AnyKind : kind.ToString().ToLowerInvariant();

    /// <summary>The sectors laid out for this wheel and kind of file, or null to keep the registration order.</summary>
    public IReadOnlyList<LayoutSlot?>? SlotsFor(WheelMode mode, MediaKind kind) =>
        Wheels.TryGetValue(WheelKey(mode), out var wheel) && wheel.TryGetValue(KindKey(mode, kind), out var slots) ? slots : null;

    public bool HasGroups(WheelMode mode) =>
        Wheels.TryGetValue(WheelKey(mode), out var wheel) && wheel.Values.Any(slots => slots.Any(s => s?.Group is not null));

    /// <summary>Whether the item is on this wheel for this kind of file, or the wheel has no layout and shows every item.</summary>
    public bool IsPlaced(WheelMode mode, MediaKind kind, string id) => SlotsFor(mode, kind) is not { } slots || slots.Any(s => s?.Holds(id) == true);

    public WheelLayout With(WheelMode mode, MediaKind kind, IReadOnlyList<LayoutSlot?> slots)
    {
        var wheels = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<LayoutSlot?>>>(Wheels);
        var wheel = new Dictionary<string, IReadOnlyList<LayoutSlot?>>(wheels.GetValueOrDefault(WheelKey(mode)) ?? new Dictionary<string, IReadOnlyList<LayoutSlot?>>())
        {
            [KindKey(mode, kind)] = slots,
        };
        wheels[WheelKey(mode)] = wheel;
        return this with { Wheels = wheels };
    }

    /// <summary>
    /// Places a user tool that has not been placed before on every wheel it belongs to, following its manifest's hint
    /// as far as the wheel allows, and remembers that it was placed, so the hint is applied once and the layout is the
    /// user's from then on. A wheel laid out for the first time starts as the registration order the wheel showed
    /// until now; otherwise the built-in items would vanish the moment a user tool arrived. Returns false when the
    /// tool found no room on some wheel: the sector the hint named, or the last, already held a full group.
    /// </summary>
    public (WheelLayout Layout, bool Placed) Install(ToolManifest manifest, IReadOnlyList<IWheelOperation> builtIns)
    {
        if (Installed.Contains(manifest.Id))
        {
            return (this, true);
        }

        var layout = this;
        var placed = true;
        var kinds = manifest.Wheel == WheelMode.Actions ? [MediaKind.Unknown] : manifest.AppliesTo ?? Enum.GetValues<MediaKind>();
        foreach (var kind in kinds)
        {
            var slots = layout.SlotsFor(manifest.Wheel, kind) ?? Seed(manifest.Wheel, kind, builtIns);
            var (next, ok) = Place(slots, manifest.Id, manifest.Placement);
            layout = layout.With(manifest.Wheel, kind, next);
            placed &= ok;
        }

        return (layout with { Installed = [.. Installed, manifest.Id] }, placed);
    }

    /// <summary>The registration order as a layout: what <see cref="WheelBuilder"/> shows for a wheel without one.</summary>
    public static IReadOnlyList<LayoutSlot?> Seed(WheelMode mode, MediaKind kind, IReadOnlyList<IWheelOperation> builtIns) =>
        Pad([.. builtIns.Where(o => o.Mode == mode && o.AppliesTo(kind)).Take(WheelModel.SlotCount).Select(o => new LayoutSlot(o.Id))]);

    /// <summary>
    /// Where a new item goes: into the group the hint names, if there is one with room; else the sector the hint
    /// names, if empty; else the first empty sector; and on a full wheel the sector the hint names, or the last,
    /// is folded into a group holding its item and the new one. A hint that names a group that does not exist yet
    /// starts that group. Only a full group leaves the item unplaced.
    /// </summary>
    public static (IReadOnlyList<LayoutSlot?> Slots, bool Placed) Place(IReadOnlyList<LayoutSlot?> slots, string id, PlacementHint? hint)
    {
        var list = Pad(slots);
        if (list.Any(s => s?.Holds(id) == true))
        {
            return (list, true);
        }

        var groupName = hint?.Group;
        if (groupName is not null && list.FindIndex(s => s?.Group is { } g && g.Name.Zh == groupName.Zh) is var found and >= 0)
        {
            var group = list[found]!.Group!;
            if (group.IsFull)
            {
                return (list, false);
            }

            list[found] = new(Group: group.Add(id));
            return (list, true);
        }

        var wanted = hint?.Index is { } index && index >= 0 && index < WheelModel.SlotCount ? index : -1;
        var empty = wanted >= 0 && list[wanted] is null ? wanted : list.IndexOf(null);
        if (empty >= 0)
        {
            list[empty] = groupName is null ? new(Item: id) : new(Group: new(groupName, [id]));
            return (list, true);
        }

        var target = wanted >= 0 ? wanted : WheelModel.SlotCount - 1;
        switch (list[target])
        {
            case { Item: { } other }:
                list[target] = new(Group: new(groupName ?? LayoutGroup.DefaultName, [other, id]));
                return (list, true);
            case { Group: { IsFull: false } group }:
                list[target] = new(Group: group.Add(id));
                return (list, true);
            default:
                return (list, false);
        }
    }

    /// <summary>
    /// Drops an item on a sector, as the layout editor does: the item leaves wherever it was on this wheel; an
    /// empty sector takes it, a sector holding an item folds the two into a group, a group with room takes it in.
    /// Returns false, and the layout unchanged, when the sector holds a full group. A wheel laid out for the first
    /// time starts as its registration order, as <see cref="Install"/> does.
    /// </summary>
    public (WheelLayout Layout, bool Placed) Put(WheelMode mode, MediaKind kind, IReadOnlyList<IWheelOperation> operations, string id, int index)
    {
        if (index < 0 || index >= WheelModel.SlotCount)
        {
            return (this, false);
        }

        var list = Slots(mode, kind, operations);
        if (list[index]?.Item == id)
        {
            return (this, true);
        }

        var target = list[index];
        if (target?.Group is { } full && full.IsFull && !full.Items.Contains(id))
        {
            return (this, false);
        }

        Take(list, id);
        list[index] = list[index] switch
        {
            null => new(Item: id),
            { Item: { } other } => new(Group: new(LayoutGroup.DefaultName, [other, id])),
            { Group: { } group } => new(Group: group.Add(id)),
            _ => new(Item: id),
        };
        return (With(mode, kind, list), true);
    }

    /// <summary>Exchanges two sectors, whatever they hold, including nothing.</summary>
    public WheelLayout Swap(WheelMode mode, MediaKind kind, IReadOnlyList<IWheelOperation> operations, int a, int b)
    {
        if (a == b || a < 0 || b < 0 || a >= WheelModel.SlotCount || b >= WheelModel.SlotCount)
        {
            return this;
        }

        var list = Slots(mode, kind, operations);
        (list[a], list[b]) = (list[b], list[a]);
        return With(mode, kind, list);
    }

    /// <summary>Takes an item off this wheel; it goes to the unplaced list. A group it leaves empty goes too.</summary>
    public WheelLayout Remove(WheelMode mode, MediaKind kind, IReadOnlyList<IWheelOperation> operations, string id)
    {
        var list = Slots(mode, kind, operations);
        return Take(list, id) ? With(mode, kind, list) : this;
    }

    /// <summary>
    /// Opens a group out onto the wheel: its first item stays in the sector, the rest take the empty sectors in
    /// order. Returns false, and the layout unchanged, when there are not enough empty sectors for all of them.
    /// </summary>
    public (WheelLayout Layout, bool Done) Ungroup(WheelMode mode, MediaKind kind, IReadOnlyList<IWheelOperation> operations, int index)
    {
        var list = Slots(mode, kind, operations);
        if (index < 0 || index >= WheelModel.SlotCount || list[index]?.Group is not { } group)
        {
            return (this, false);
        }

        var empties = Enumerable.Range(0, WheelModel.SlotCount).Where(i => list[i] is null).ToList();
        if (group.Items.Count - 1 > empties.Count)
        {
            return (this, false);
        }

        list[index] = new(Item: group.Items[0]);
        foreach (var (item, slot) in group.Items.Skip(1).Zip(empties))
        {
            list[slot] = new(Item: item);
        }

        return (With(mode, kind, list), true);
    }

    /// <summary>Renames the group in a sector; nothing happens when the sector holds no group or the name is empty.</summary>
    public WheelLayout RenameGroup(WheelMode mode, MediaKind kind, IReadOnlyList<IWheelOperation> operations, int index, LocalizedText name)
    {
        var list = Slots(mode, kind, operations);
        if (index < 0 || index >= WheelModel.SlotCount || list[index]?.Group is not { } group || string.IsNullOrWhiteSpace(name.Zh))
        {
            return this;
        }

        list[index] = new(Group: group with { Name = name });
        return With(mode, kind, list);
    }

    /// <summary>Forgets this wheel's layout, so it shows the registration order again.</summary>
    public WheelLayout Reset(WheelMode mode, MediaKind kind)
    {
        if (!Wheels.TryGetValue(WheelKey(mode), out var wheel) || !wheel.ContainsKey(KindKey(mode, kind)))
        {
            return this;
        }

        var wheels = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<LayoutSlot?>>>(Wheels);
        var kinds = new Dictionary<string, IReadOnlyList<LayoutSlot?>>(wheel);
        kinds.Remove(KindKey(mode, kind));
        if (kinds.Count == 0)
        {
            wheels.Remove(WheelKey(mode));
        }
        else
        {
            wheels[WheelKey(mode)] = kinds;
        }

        return this with { Wheels = wheels };
    }

    /// <summary>The wheel's six sectors to edit: its layout, or the registration order when it has none yet.</summary>
    private List<LayoutSlot?> Slots(WheelMode mode, MediaKind kind, IReadOnlyList<IWheelOperation> operations) =>
        Pad(SlotsFor(mode, kind) ?? Seed(mode, kind, operations));

    /// <summary>Removes the item from whichever sector or group holds it; a group left empty goes too. False when it was not there.</summary>
    private static bool Take(List<LayoutSlot?> list, string id)
    {
        var at = list.FindIndex(s => s?.Holds(id) == true);
        if (at < 0)
        {
            return false;
        }

        list[at] = list[at] switch
        {
            { Group: { } group } when group.Items.Count > 1 => new(Group: group with { Items = [.. group.Items.Where(i => i != id)] }),
            _ => null,
        };
        return true;
    }

    private static List<LayoutSlot?> Pad(IReadOnlyList<LayoutSlot?> slots)
    {
        List<LayoutSlot?> list = [.. slots.Take(WheelModel.SlotCount)];
        while (list.Count < WheelModel.SlotCount)
        {
            list.Add(null);
        }

        return list;
    }

    /// <summary>A hand-edited file can leave out sections or write a slot that names nothing; those are dropped.</summary>
    public WheelLayout Repair()
    {
        var wheels = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<LayoutSlot?>>>();
        foreach (var (wheelKey, wheel) in Wheels ?? new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<LayoutSlot?>>>())
        {
            if (wheel is null)
            {
                continue;
            }

            var kinds = new Dictionary<string, IReadOnlyList<LayoutSlot?>>();
            foreach (var (kindKey, slots) in wheel)
            {
                if (slots is not null)
                {
                    kinds[kindKey] = [.. slots.Select(Clean)];
                }
            }

            wheels[wheelKey] = kinds;
        }

        return new(CurrentVersion, wheels, [.. (Installed ?? []).Where(id => id is not null)]);
    }

    private static LayoutSlot? Clean(LayoutSlot? slot) => slot switch
    {
        { Item: { Length: > 0 } } => slot,
        { Group: { Name.Zh: { Length: > 0 }, Items: { Count: > 0 } items } group } => new(Group: group with { Items = [.. items.Where(i => !string.IsNullOrEmpty(i)).Take(LayoutGroup.Capacity)] }),
        _ => null,
    };
}
