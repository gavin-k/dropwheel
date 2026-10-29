using System.Diagnostics;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    /// <summary>
    /// The wheels the earlier scenarios opened were checked on screen and judged drawn: the first show is
    /// always checked, so at least one verdict is in the log, and none of them was a white block. A first
    /// look that was flat and a second that was not is reported, not failed.
    /// </summary>
    private static void FrameChecked(Checks checks, AppLog log)
    {
        var drawn = log.FindSinceStart("overlay: frame check drawn");
        var flat = log.FindSinceStart("overlay: frame check flat");
        var told = log.FindSinceStart("tray: notification sent: 轮盘显示成了一整块白色");
        var second = log.FindSinceStart("overlay: frame check looks flat");
        checks.Check("the wheel's frame check finds it drawn on screen", drawn is not null && flat is null && told is null,
            $"{drawn ?? log.FindSinceStart("overlay: frame check") ?? "no 'frame check' line"}"
            + string.Concat(new[] { second, flat, told }.OfType<string>().Select(line => $"; {line}")));
    }

    /// <summary>
    /// A wheel that reaches the screen as a flat white block, as a stuck graphics driver shows it
    /// (<c>--white-wheel</c>), is judged flat and the tray tells the user how to fix it. Runs its own
    /// instance after the main one quit: <c>--wheel-preview</c> shows the wheel once and quits.
    /// </summary>
    private static void WhiteWheel(Checks checks, string exe)
    {
        var profile = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "profile");
        var log = new AppLog();
        using var app = Process.Start(new ProcessStartInfo(exe, ["--data-dir", profile, "--wheel-preview", "--white-wheel"]) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })
            ?? throw new InvalidOperationException($"could not start {exe}");
        var exited = app.WaitForExit(30000);
        if (!exited)
        {
            app.Kill();
        }

        log.Poll();
        var flat = log.FindSinceStart("overlay: frame check flat");
        var told = log.FindSinceStart("tray: notification sent: 轮盘显示成了一整块白色");
        var error = log.FindSinceStart(" ERROR ");
        checks.Check("a wheel shown as a white block is caught and the tray says how to fix it", exited && flat is not null && told is not null && error is null,
            $"{(exited ? "preview quit by itself" : "still running after 30 s, killed")}; {flat ?? log.FindSinceStart("overlay: frame check") ?? "no 'frame check' line"}; "
            + $"{told ?? log.FindSinceStart("tray: ") ?? "no tray line"}{(error is null ? "" : $"; {error}")}");
    }
}
