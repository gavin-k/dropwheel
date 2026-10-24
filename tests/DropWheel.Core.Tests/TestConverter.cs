using DropWheel.Core.Conversion;

namespace DropWheel.Core.Tests;

/// <summary>Engine double: declares the given edges and runs whatever the test supplies.</summary>
internal sealed class TestConverter(IEnumerable<ConversionEdge> edges, Func<ConversionRequest, IProgress<double>, CancellationToken, Task<ConversionResult>>? run = null) : IConverter
{
    public string Id => "test";

    public IEnumerable<ConversionEdge> Edges { get; } = [.. edges];

    public Availability Check(ConversionRequest request) => Availability.Available;

    public Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
        => run is null ? Task.FromResult(new ConversionResult(true, [])) : run(request, progress, cancellationToken);

    public static IEnumerable<ConversionEdge> Between(params string[] formats)
        => from a in formats from b in formats where a != b select new ConversionEdge(a, b);
}
