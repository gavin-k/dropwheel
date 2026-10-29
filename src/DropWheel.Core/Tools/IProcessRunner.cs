namespace DropWheel.Core.Tools;

/// <param name="Executable">A full path; see <see cref="CommandResolver"/>.</param>
/// <param name="Environment">Variables added to the process's environment.</param>
/// <param name="Timeout">After this the process tree is killed and the outcome says <see cref="ProcessOutcome.TimedOut"/>.</param>
public sealed record ProcessStart(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout);

/// <param name="ExitCode">-1 when the process was killed for the timeout.</param>
/// <param name="StderrTail">The last lines the process wrote to stderr, newline-joined, for the error message.</param>
public sealed record ProcessOutcome(int ExitCode, string StderrTail, bool TimedOut = false);

/// <summary>
/// Runs a user tool's process. The Windows implementation lives in Engines on top of the sidecar launcher; tests
/// fake it, so Core never starts a process itself.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Runs to completion, handing each stdout line to <paramref name="onStdoutLine"/>; cancellation kills the process tree.</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The executable could not be started.</exception>
    Task<ProcessOutcome> RunAsync(ProcessStart start, Action<string>? onStdoutLine, CancellationToken cancellationToken);
}
