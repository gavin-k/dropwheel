using System.Diagnostics;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    /// <summary>
    /// The task window's buttons, pressed the way a screen reader would: a JPG another program holds open fails
    /// to convert, and 重试 once it is free makes the PNG; 打开位置 opens its folder in Explorer with the PNG
    /// selected; 取消 on a long video encode stops ffmpeg and leaves no output behind.
    /// </summary>
    private static void QueueButtons(Checks checks, AppLog log, string exe, bool explorer)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "queue");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir))
        {
            File.Delete(old);
        }

        if (RetryLocked(checks, log, dir, explorer) is var (label, png))
        {
            ShowInFolder(checks, log, label, png);
        }

        CancelEncode(checks, log, exe, dir, explorer);
    }

    private static (string Label, string Output)? RetryLocked(Checks checks, AppLog log, string dir, bool explorer)
    {
        var jpg = Path.Combine(dir, $"m1r-{DateTime.Now:HHmmss}.jpg");
        WriteJpeg(jpg, 320, 200);
        FileStream? held = null;
        try
        {
            // Opened without sharing just before the drop, the way an editor that is saving the file holds it.
            var label = DropOnWheel(checks, log, jpg, "png", explorer,
                beforeRelease: () => held = new FileStream(jpg, FileMode.Open, FileAccess.Read, FileShare.None));
            if (label is null)
            {
                return null;
            }

            var failed = log.WaitFor(30000, $"queue: {label} failed");
            if (!checks.Check("a JPG another program holds open fails to convert", failed is not null, failed ?? log.Find($"queue: {label} ") ?? "no queue outcome within 30 s"))
            {
                return null;
            }

            held?.Dispose();
            held = null;
            log.Mark();
            var pressed = Invoke(QueueTitle, $"重试 {label}", out var how);
            var done = log.WaitFor(30000, $"queue: {label} done");
            var output = done is null ? null : OutputsRegex().Match(done) is { Success: true } m ? m.Groups[1].Value : null;
            var made = output is not null && File.Exists(output);
            checks.Check("重试 once the file is free converts it", pressed && made,
                $"{how}; {log.Find("queue: retrying ") ?? "no 'retrying' line"}; {done ?? "no 'done' line within 30 s"}");
            return made ? (label, output!) : null;
        }
        finally
        {
            held?.Dispose();
        }
    }

    private static void ShowInFolder(Checks checks, AppLog log, string label, string output)
    {
        var folder = Path.GetDirectoryName(output)!;
        log.Mark();
        var pressed = Invoke(QueueTitle, $"打开位置 {label}", out var how);
        var shown = log.WaitFor(5000, "queue: showed ");
        var window = ExplorerSource.FolderWindow(folder, 10000);
        var item = $"no Explorer window titled '{Path.GetFileName(folder)}'";
        var selected = !window.IsNull && ExplorerSource.IsSelected(window, output, out item);
        checks.Check("打开位置 opens the folder in Explorer with the output selected", pressed && shown is not null && selected,
            $"{how}; {shown ?? log.Find("queue: could not show") ?? "no 'showed' line"}; {item}");
        ExplorerSource.CloseAndWait(window);
    }

    private static void CancelEncode(Checks checks, AppLog log, string exe, string dir, bool explorer)
    {
        const string Name = "取消 stops a running encode and leaves no output";
        var mp4 = Path.Combine(dir, $"m1c-{DateTime.Now:HHmmss}.mp4");
        var ffmpeg = Path.Combine(Path.GetDirectoryName(exe)!, "sidecars", "ffmpeg", "ffmpeg.exe");

        // 30 s of 720p: a WebM encode of it takes long enough to be canceled halfway on every test machine.
        using (var make = Process.Start(new ProcessStartInfo(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-t", "30", "-c:v", "mpeg4", "-q:v", "6", "-y", mp4])
            { UseShellExecute = false, CreateNoWindow = true }))
        {
            if (make is not null && !make.WaitForExit(60000))
            {
                make.Kill();
            }
        }

        if (!File.Exists(mp4))
        {
            checks.Check(Name, false, $"could not make the test video with {ffmpeg}");
            return;
        }

        var before = FfmpegIds();
        if (DropOnWheel(checks, log, mp4, "webm", explorer) is not { } label)
        {
            return;
        }

        // Pressed once ffmpeg runs, so there is a process to stop and not just a waiting job to drop.
        HashSet<int> encoders = [];
        Checks.Wait(() => (encoders = [.. FfmpegIds().Except(before)]).Count > 0, 10000);
        log.Mark();
        var pressed = Invoke(QueueTitle, $"取消 {label}", out var how);
        var canceled = log.WaitFor(5000, $"queue: {label} canceled");
        var stopped = Checks.Wait(() => !FfmpegIds().Overlaps(encoders), 3000);
        var stem = Path.GetFileNameWithoutExtension(mp4);
        var left = Directory.GetFiles(dir).Select(Path.GetFileName).Where(f => f!.Contains(stem, StringComparison.OrdinalIgnoreCase) && f != Path.GetFileName(mp4)).ToList();
        checks.Check(Name, pressed && encoders.Count > 0 && canceled is not null && stopped && left.Count == 0,
            $"{how}; ffmpeg {(encoders.Count == 0 ? "never started" : $"{string.Join(",", encoders)} {(stopped ? "gone" : "still running")}")}; "
            + $"{canceled ?? log.Find($"queue: {label} ") ?? "no 'canceled' line within 5 s"}; left {(left.Count == 0 ? "nothing" : string.Join(", ", left))}");

        foreach (var id in encoders.Intersect(FfmpegIds()))
        {
            using var stray = Process.GetProcessById(id);
            stray.Kill();
        }
    }

    private static HashSet<int> FfmpegIds()
    {
        var all = Process.GetProcessesByName("ffmpeg");
        try
        {
            return [.. all.Select(p => p.Id)];
        }
        finally
        {
            foreach (var p in all)
            {
                p.Dispose();
            }
        }
    }
}
