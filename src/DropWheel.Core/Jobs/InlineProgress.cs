namespace DropWheel.Core.Jobs;

/// <summary>Reports synchronously on the caller's thread; <see cref="Progress{T}"/> would post to a captured context.</summary>
public sealed class InlineProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
