using System.Threading.Channels;

namespace DropWheel.App.Diagnostics;

/// <summary>
/// Append-only text log under %LOCALAPPDATA%\DropWheel\logs, one file per day. Writing only queues the
/// line: callers include the hook thread, where a blocking file write could get the hook removed.
/// </summary>
internal static class FileLog
{
    private static readonly Channel<string> Lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private static readonly Task Writer = Task.Run(WriteAsync);

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropWheel", "logs");

    public static string CurrentFile => Path.Combine(Directory, $"dropwheel-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    /// <summary>Waits until queued lines are on disk. Call once on exit.</summary>
    public static void Flush(TimeSpan timeout)
    {
        Lines.Writer.TryComplete();
        Writer.Wait(timeout);
    }

    private static void Write(string level, string message) =>
        Lines.Writer.TryWrite($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {level} {message}");

    private static async Task WriteAsync()
    {
        await foreach (var line in Lines.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                await File.AppendAllTextAsync(CurrentFile, line + Environment.NewLine).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Logging must never take the app down; a locked or full disk loses the line.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
