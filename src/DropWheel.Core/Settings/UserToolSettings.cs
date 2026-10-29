using System.Collections.Immutable;

namespace DropWheel.Core.Settings;

/// <summary>
/// Which user tools the user has switched on in Settings › 我的工具, each with the command hash it was switched
/// on for: a script tool runs code DropWheel did not write, so a new tool, or one whose command changed since,
/// is greyed until the user looks at it and switches it on again.
/// </summary>
public sealed record UserToolSettings(ImmutableSortedDictionary<string, string> Trusted)
{
    public static UserToolSettings Default { get; } = new(ImmutableSortedDictionary<string, string>.Empty);

    public static string UntrustedReason => L.T("在 设置 › 我的工具 里启用后可用");

    public bool IsTrusted(string id, string key) => Trusted.TryGetValue(id, out var trusted) && trusted == key;

    public UserToolSettings Trust(string id, string key) => new(Trusted.SetItem(id, key));

    public UserToolSettings Distrust(string id) => new(Trusted.Remove(id));

    public bool Equals(UserToolSettings? other) => other is not null && Trusted.SequenceEqual(other.Trusted);

    public override int GetHashCode() => Trusted.Aggregate(0, (h, p) => HashCode.Combine(h, p.Key, p.Value));
}
