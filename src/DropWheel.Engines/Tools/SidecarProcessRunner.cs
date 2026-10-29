using DropWheel.Core.Tools;
using DropWheel.Engines.Media;

namespace DropWheel.Engines.Tools;

/// <summary>
/// Runs a user tool's script the way the engines run ffmpeg: no shell, UTF-8 pipes, below normal priority, the
/// process tree killed on cancellation; plus the manifest's timeout, and a kill-on-close job so nothing outlives DropWheel.
/// </summary>
public sealed class SidecarProcessRunner : IProcessRunner
{
    public async Task<ProcessOutcome> RunAsync(ProcessStart start, Action<string>? onStdoutLine, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(start.Timeout);
        try
        {
            var result = await new SidecarProcess(start.Executable)
                .RunAsync(start.Arguments, onStdoutLine, timeout.Token, start.WorkingDirectory, start.Environment, KillOnCloseJob.Assign)
                .ConfigureAwait(false);
            return new(result.ExitCode, string.Join('\n', result.StderrTail));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(-1, "", TimedOut: true);
        }
    }
}
