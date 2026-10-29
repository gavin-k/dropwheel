using System.Collections;
using DropWheel.Core.Tools;

namespace DropWheel.Core.Wheel;

/// <summary>
/// The built-in operations followed by whatever the user's tools folder holds right now, read afresh on every
/// enumeration, so a tool written while DropWheel runs is on the next wheel that opens.
/// </summary>
public sealed class LiveOperations(IReadOnlyList<IWheelOperation> builtIns, UserToolCatalog catalog) : IReadOnlyList<IWheelOperation>
{
    private IReadOnlyList<IWheelOperation> Snapshot => [.. builtIns, .. catalog.Current.Tools];

    public IWheelOperation this[int index] => Snapshot[index];

    public int Count => Snapshot.Count;

    public IEnumerator<IWheelOperation> GetEnumerator() => Snapshot.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
