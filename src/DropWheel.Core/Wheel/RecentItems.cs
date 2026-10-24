using DropWheel.Core.Conversion;

namespace DropWheel.Core.Wheel;

/// <summary>
/// Most-recently-used items per (media kind, wheel mode), newest first. In memory for M0; it moves
/// to the settings store once there is one.
/// </summary>
public sealed class RecentItems(int capacity = 3) : IRecentItems
{
    private readonly Dictionary<(MediaKind, WheelMode), List<string>> _lists = [];
    private readonly Lock _gate = new();

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
            if (list.Count > capacity)
            {
                list.RemoveRange(capacity, list.Count - capacity);
            }
        }
    }
}
