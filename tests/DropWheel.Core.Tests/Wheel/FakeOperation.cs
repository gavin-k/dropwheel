using DropWheel.Core.Conversion;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Tests.Wheel;

internal sealed class FakeOperation(string id, WheelMode mode, IReadOnlyList<MediaKind>? kinds) : IWheelOperation
{
    public string Id => id;
    public WheelMode Mode => mode;
    public string Label => id;
    public string Hint => "";
    public Availability Availability { get; init; } = Availability.Available;
    public bool AppliesTo(MediaKind kind) => kinds is null || kinds.Contains(kind);
    public Availability Check(ConversionRequest request) => Availability;
    public Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken) => throw new NotSupportedException();
}
