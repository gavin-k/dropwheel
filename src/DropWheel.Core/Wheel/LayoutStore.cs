using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;
using DropWheel.Core.Tools;

namespace DropWheel.Core.Wheel;

/// <summary>layout.json on disk; every change is written at once. See <see cref="WheelLayout"/>.</summary>
public sealed class LayoutStore
{
    private readonly string _path;
    private readonly Lock _gate = new();

    public LayoutStore(string path)
    {
        _path = path;
        var (loaded, problem) = JsonFile.Load(path, SettingsJson.Default.WheelLayout, WheelLayout.Empty);
        Current = loaded.Repair();
        LoadProblem = problem;
    }

    public WheelLayout Current { get; private set; }

    /// <summary>Why the file on disk was not used, or null.</summary>
    public string? LoadProblem { get; }

    public event Action<WheelLayout>? Changed;

    public WheelLayout Update(Func<WheelLayout, WheelLayout> change)
    {
        WheelLayout next;
        lock (_gate)
        {
            next = change(Current);
            if (ReferenceEquals(next, Current))
            {
                return next;
            }

            JsonFile.Save(_path, next, SettingsJson.Default.WheelLayout);
            Current = next;
        }

        Changed?.Invoke(next);
        return next;
    }

    public IReadOnlyList<LayoutSlot?>? SlotsFor(WheelMode mode, MediaKind kind) => Current.SlotsFor(mode, kind);

    public bool HasGroups(WheelMode mode) => Current.HasGroups(mode);

    /// <summary>See <see cref="WheelLayout.Install"/>; a tool placed before is left where the user has it.</summary>
    public bool Install(ToolManifest manifest, IReadOnlyList<IWheelOperation> builtIns)
    {
        var placed = true;
        Update(layout =>
        {
            var (next, ok) = layout.Install(manifest, builtIns);
            placed = ok;
            return next;
        });
        return placed;
    }

    /// <summary>The operations some laid-out wheel they belong to leaves off, so Settings can say so.</summary>
    public IReadOnlyList<string> UnplacedIds(IEnumerable<IWheelOperation> operations)
    {
        var layout = Current;
        return [.. operations.Where(o => Enum.GetValues<MediaKind>().Any(kind => o.AppliesTo(kind) && !layout.IsPlaced(o.Mode, kind, o.Id))).Select(o => o.Id).Distinct()];
    }
}
