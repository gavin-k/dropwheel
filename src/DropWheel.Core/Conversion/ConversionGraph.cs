namespace DropWheel.Core.Conversion;

/// <summary>Routes (from, to) pairs to the engine that handles them.</summary>
public sealed class ConversionGraph
{
    private readonly Dictionary<ConversionEdge, IConverter> _edges = new();

    public void Register(IConverter converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        foreach (var edge in converter.Edges)
        {
            // First registered engine wins; later engines can only fill gaps.
            _edges.TryAdd(edge, converter);
        }
    }

    public IConverter? Resolve(string fromFormatId, string toFormatId)
        => _edges.TryGetValue(new ConversionEdge(fromFormatId, toFormatId), out var c) ? c : null;

    /// <summary>All formats reachable from <paramref name="fromFormatId"/>, including input-only targets (so they can be greyed).</summary>
    public IEnumerable<string> TargetsFrom(string fromFormatId)
        => _edges.Keys.Where(e => e.FromFormatId == fromFormatId).Select(e => e.ToFormatId).Distinct();

    public Availability Check(ConversionRequest request)
    {
        var from = FormatRegistry.FromPath(request.SourcePaths[0]);
        var to = FormatRegistry.FromExtension(request.TargetFormatId);

        if (to is null)
        {
            return Availability.Unavailable("未知的目标格式。");
        }

        if (!to.CanOutput)
        {
            return Availability.Unavailable(to.InputOnlyReason ?? $"{to.DisplayName} 仅支持作为输入。") with { Cause = UnavailableCause.InputOnly };
        }

        if (from is null)
        {
            return Availability.Unavailable("无法识别源文件格式。");
        }

        var converter = Resolve(from.Id, to.Id);
        return converter is null
            ? Availability.Unavailable($"没有引擎支持 {from.DisplayName} → {to.DisplayName}。") with { Cause = UnavailableCause.MissingOptionalModule }
            : converter.Check(request);
    }
}
