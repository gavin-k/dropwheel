using System.Diagnostics;

namespace DropWheel.Engines.Media;

/// <summary>
/// Launches an external engine (ffmpeg.exe, 7za.exe, soffice.exe) with a per-job temp working directory.
/// Keeps LGPL / MPL binaries at a process boundary from the MIT main program (see ADR-0003).
/// </summary>
public sealed class SidecarProcess(string executablePath)
{
    public string ExecutablePath { get; } = executablePath;

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string>? onStderrLine,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(ExecutablePath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动 {ExecutablePath}");
        using var reg = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
        });

        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                onStderrLine?.Invoke(line);
            }
        }, cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stderr.ConfigureAwait(false);
        return process.ExitCode;
    }
}
