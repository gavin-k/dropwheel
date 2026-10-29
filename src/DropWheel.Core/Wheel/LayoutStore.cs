using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;
using DropWheel.Core.Tools;

namespace DropWheel.Core.Wheel;

/// <summary>
/// layout.json on disk; every change is written at once. The file is also the agent's: it may be edited by hand or by
/// an agent while DropWheel runs, so a change is applied to what is on disk now, not to what was loaded, and
/// <see cref="Watch"/> picks up outside edits. See <see cref="WheelLayout"/>.
/// </summary>
public sealed class LayoutStore : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private DateTime _stamp;
    private FileSystemWatcher? _watcher;
    private ITimer? _debounce;

    public LayoutStore(string path, TimeProvider? time = null)
    {
        _path = Path.GetFullPath(path);
        _time = time ?? TimeProvider.System;
        Current = Load();
    }

    public WheelLayout Current { get; private set; }

    /// <summary>Why the file on disk was not used, or null; of the last load attempt.</summary>
    public string? LoadProblem { get; private set; }

    public event Action<WheelLayout>? Changed;

    /// <summary>Applies the change to the layout on disk, which may be newer than <see cref="Current"/>.</summary>
    public WheelLayout Update(Func<WheelLayout, WheelLayout> change)
    {
        WheelLayout next;
        bool moved;
        lock (_gate)
        {
            moved = ReloadLocked();
            next = change(Current);
            if (!ReferenceEquals(next, Current))
            {
                JsonFile.Save(_path, next, SettingsJson.Default.WheelLayout);
                _stamp = Stamp();
                Current = next;
                moved = true;
            }
        }

        if (moved)
        {
            Changed?.Invoke(next);
        }

        return next;
    }

    /// <summary>Reads outside edits; on I/O failure keeps the current layout and retries if watching.</summary>
    public bool Reload()
    {
        bool moved;
        WheelLayout current;
        lock (_gate)
        {
            try
            {
                moved = ReloadLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoadProblem = ex.Message;
                _debounce?.Change(Debounce, Timeout.InfiniteTimeSpan);
                return false;
            }

            current = Current;
        }

        if (moved)
        {
            Changed?.Invoke(current);
        }

        return moved;
    }

    /// <summary>Reloads shortly after the file is written by someone else; our own writes are already current.</summary>
    public void Watch()
    {
        if (_watcher is not null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        _debounce = _time.CreateTimer(_ => Reload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += OnChange;
        _watcher.Changed += OnChange;
        _watcher.Deleted += OnChange;
        _watcher.Renamed += OnChange;
        _watcher.Error += OnChange;
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
        }
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

    private void OnChange(object sender, EventArgs e)
    {
        lock (_gate)
        {
            _debounce?.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private bool ReloadLocked()
    {
        if (Stamp() == _stamp)
        {
            return false;
        }

        Current = Load();
        return true;
    }

    private WheelLayout Load()
    {
        var stamp = Stamp();
        var (loaded, problem) = JsonFile.Load(_path, SettingsJson.Default.WheelLayout, WheelLayout.Empty);
        var repaired = loaded.Repair();
        _stamp = stamp;
        LoadProblem = problem;
        return repaired;
    }

    /// <summary>When the file was last written, or the fixed value File gives a missing one.</summary>
    private DateTime Stamp() => File.GetLastWriteTimeUtc(_path);
}
