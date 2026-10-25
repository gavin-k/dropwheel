using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;

namespace DropWheel.Core.Wheel;

/// <summary>
/// Most-recently-used items per (media kind, wheel mode), newest first. With a <paramref name="path"/>
/// the lists are loaded from it and saved back after every <see cref="Touch"/>.
/// </summary>
public sealed class RecentItems : IRecentItems
{
    private readonly Dictionary<(MediaKind, WheelMode), List<string>> _lists = [];
    private readonly Lock _gate = new();
    private readonly int _capacity;
    private readonly string? _path;

    public RecentItems(int capacity = 3, string? path = null)
    {
        _capacity = capacity;
        _path = path;
        if (path is null)
        {
            return;
        }

        var (saved, problem) = JsonFile.Load(path, SettingsJson.Default.DictionaryStringListString, []);
        LoadProblem = problem;
        foreach (var (key, items) in saved)
        {
            var parts = key.Split('.');
            if (parts.Length == 2 && Enum.TryParse<MediaKind>(parts[0], out var kind) && Enum.TryParse<WheelMode>(parts[1], out var mode))
            {
                _lists[(kind, mode)] = [.. items.Distinct().Take(capacity)];
            }
        }
    }

    /// <summary>Why the file on disk was not used, or null.</summary>
    public string? LoadProblem { get; }

    public IEnumerable<string> For(MediaKind kind, WheelMode mode)
    {
        lock (_gate)
        {
            return _lists.TryGetValue((kind, mode), out var list) ? [.. list] : [];
        }
    }

    public void Touch(MediaKind kind, WheelMode mode, string itemId)
    {
        lock (_gate)
        {
            if (!_lists.TryGetValue((kind, mode), out var list))
            {
                _lists[(kind, mode)] = list = [];
            }

            list.Remove(itemId);
            list.Insert(0, itemId);
            if (list.Count > _capacity)
            {
                list.RemoveRange(_capacity, list.Count - _capacity);
            }

            if (_path is not null)
            {
                var snapshot = _lists.ToDictionary(e => $"{e.Key.Item1}.{e.Key.Item2}", e => e.Value.ToList());
                JsonFile.Save(_path, snapshot, SettingsJson.Default.DictionaryStringListString);
            }
        }
    }
}
