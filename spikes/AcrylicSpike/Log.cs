using System.Diagnostics;
using System.Text;

namespace AcrylicSpike;

/// <summary>Timestamped log to a file (stdout is useless for a WinExe started via prlctl). Timestamps are ms since process start.</summary>
internal static class Log
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    public static string? FilePath { get; private set; }

    public static double NowMs => Clock.Elapsed.TotalMilliseconds;

    public static void Open(string directory, string prefix)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _file = new StreamWriter(FilePath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public static void Info(string message)
    {
        var line = $"[{NowMs,9:F1} ms] [T{Environment.CurrentManagedThreadId,2}] {message}";
        lock (Gate)
        {
            _file?.WriteLine(line);
        }
    }
}
