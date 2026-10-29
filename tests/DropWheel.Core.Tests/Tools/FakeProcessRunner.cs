using DropWheel.Core.Tools;

namespace DropWheel.Core.Tests.Tools;

/// <summary>Stands in for the script: records each start and runs <see cref="Behavior"/> instead of a process.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessStart> Starts { get; } = [];

    /// <summary>What the "script" does: gets the start and a line printer, returns the outcome. Default: exit 0.</summary>
    public Func<ProcessStart, Action<string>, CancellationToken, Task<ProcessOutcome>> Behavior { get; set; } =
        (_, _, _) => Task.FromResult(new ProcessOutcome(0, ""));

    public Task<ProcessOutcome> RunAsync(ProcessStart start, Action<string>? onStdoutLine, CancellationToken cancellationToken)
    {
        Starts.Add(start);
        return Behavior(start, onStdoutLine ?? (_ => { }), cancellationToken);
    }

    /// <summary>The value of the placeholder-expanded argument after <paramref name="flag"/>.</summary>
    public static string After(ProcessStart start, string flag) => start.Arguments[start.Arguments.ToList().IndexOf(flag) + 1];
}
