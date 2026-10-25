using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DropWheel.Engines.Media;

/// <param name="StderrTail">The last lines the process wrote to stderr, for the error message.</param>
public sealed record SidecarResult(int ExitCode, IReadOnlyList<string> StderrTail);

/// <summary>
/// Launches an external engine (ffmpeg.exe, soffice.exe) at below normal priority, so a long encode does not
/// make the desktop stutter. Keeps LGPL / MPL binaries at a process boundary from the MIT main program (see ADR-0003).
/// </summary>
public sealed class SidecarProcess(string executablePath)
{
    private const int TailLines = 20;

    public string ExecutablePath { get; } = executablePath;

    /// <summary>
    /// Runs the process to completion, handing each stdout line to <paramref name="onStdoutLine"/>. Both pipes are
    /// drained to the end, so a chatty process never blocks on a full pipe. On cancellation the process tree is
    /// killed and awaited before the exception propagates, so the caller can delete what it was writing.
    /// </summary>
    /// <exception cref="Win32Exception">The executable could not be started.</exception>
    public async Task<SidecarResult> RunAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(ExecutablePath)
        {
            // Not the app's directory: a process holds its working directory open, which would block deleting it.
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new Win32Exception($"无法启动 {ExecutablePath}");
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Already exited, or not allowed to change it; normal priority is fine.
        }

        var tail = new Queue<string>();
        var stdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                onStdoutLine?.Invoke(line);
            }
        });
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                tail.Enqueue(line);
                if (tail.Count > TailLines)
                {
                    tail.Dequeue();
                }
            }
        });

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Exited on its own in the meantime.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return new(process.ExitCode, tail.ToArray());
    }
}
