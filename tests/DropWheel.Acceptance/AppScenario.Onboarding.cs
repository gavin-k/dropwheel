using System.Diagnostics;
using System.Globalization;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    private const string GuideTitle = "DropWheel 入门";

    /// <summary>
    /// Started the way the Run value starts it, with --at-sign-in, DropWheel stays in the tray even with no
    /// settings file yet: no guide, no task window. With no language picked it speaks the one Windows shows.
    /// </summary>
    private static void AtSignIn(Checks checks, string exe)
    {
        var profile = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "at-sign-in");
        if (Directory.Exists(profile))
        {
            Directory.Delete(profile, recursive: true);
        }

        Directory.CreateDirectory(profile);
        var log = new AppLog();
        using var app = Process.Start(new ProcessStartInfo(exe, ["--data-dir", profile, "--at-sign-in"]) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })
            ?? throw new InvalidOperationException($"could not start {exe}");
        try
        {
            var ready = log.WaitFor(30000, "ready:");
            var guide = PInvoke.FindWindow(null, GuideTitle);
            var queue = PInvoke.FindWindow(null, QueueTitle);
            var shown = (!guide.IsNull && PInvoke.IsWindowVisible(guide)) || (!queue.IsNull && PInvoke.IsWindowVisible(queue));
            var signIn = log.FindSinceStart("started by Windows at sign-in");
            checks.Check("started with --at-sign-in and no settings yet, DropWheel stays in the tray", ready is not null && signIn is not null && !shown,
                $"{(ready is null ? "no 'ready:' line within 30 s" : signIn ?? "no 'started by Windows at sign-in' line")}; guide or task window visible={shown}");

            var windows = CultureInfo.CurrentUICulture;
            var expected = $"language: {(windows.TwoLetterISOLanguageName == "zh" ? "Chinese" : "English")} (setting System, Windows {windows.Name})";
            var language = log.FindSinceStart(expected);
            var refused = log.FindSinceStart("language: Windows refused");
            checks.Check("with no language picked, DropWheel speaks the language Windows shows", language is not null && refused is null,
                $"{language ?? log.FindSinceStart("language: ") ?? "no 'language:' line"}, expected '{expected}'{(refused is null ? "" : "; " + refused)}");
        }
        finally
        {
            using (var quit = Process.Start(new ProcessStartInfo(exe, ["--quit"]) { UseShellExecute = false }))
            {
                quit?.WaitForExit(10000);
            }

            if (!app.WaitForExit(10000))
            {
                app.Kill();
            }
        }
    }

    /// <summary>
    /// A first launch, with nothing in settings but the language, opens the guide instead of the task window. 试一试 puts a sample
    /// photo in Pictures and selects it in Explorer; converting that photo to PNG is reported in the guide;
    /// 开始使用 hides the guide and saves that it was seen.
    /// </summary>
    private static void FirstRun(Checks checks, string exe, bool explorer)
    {
        var profile = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "first-run");
        if (Directory.Exists(profile))
        {
            Directory.Delete(profile, recursive: true);
        }

        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), """{ "language": "Chinese" }""");
        var samples = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DropWheel 示例");
        if (Directory.Exists(samples))
        {
            Directory.Delete(samples, recursive: true);
        }

        var log = new AppLog();
        using var app = Process.Start(new ProcessStartInfo(exe, ["--data-dir", profile]) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })
            ?? throw new InvalidOperationException($"could not start {exe}");
        try
        {
            var ready = log.WaitFor(30000, "ready:");
            var guide = PInvoke.FindWindow(null, GuideTitle);
            var queue = PInvoke.FindWindow(null, QueueTitle);
            var guideShown = !guide.IsNull && PInvoke.IsWindowVisible(guide);
            var queueShown = !queue.IsNull && PInvoke.IsWindowVisible(queue);
            if (!checks.Check("a first launch opens the guide, not the task window", ready is not null && guideShown && !queueShown,
                $"{(ready is null ? "no 'ready:' line within 30 s" : log.Find("onboarding: shown") ?? "no 'onboarding: shown' line")}; guide visible={guideShown}, task window visible={queueShown}"))
            {
                return;
            }

            var sample = TrySample(checks, log, samples);
            if (sample is null)
            {
                return;
            }

            if (DropOnWheel(checks, log, sample, "png", explorer) is not { } label)
            {
                return;
            }

            var saw = log.WaitFor(30000, $"onboarding: saw {label} ");
            var outcome = Element(GuideTitle, "outcome", out var how, byId: true)?.CurrentName.ToString();
            checks.Check("converting it while the guide is open says so in the guide", saw?.Contains(" done", StringComparison.Ordinal) == true && outcome?.StartsWith("转换成功", StringComparison.Ordinal) == true,
                $"{saw ?? log.Find($"queue: {label} ") ?? "no outcome within 30 s"}; {(outcome is null ? how : $"guide says '{outcome}'")}");

            log.Mark();
            var pressed = Invoke(GuideTitle, "开始使用", out how);
            var done = log.WaitFor(5000, "onboarding: done");
            var hidden = Checks.Wait(() => !PInvoke.IsWindowVisible(guide), 3000);
            var file = Path.Combine(profile, "settings.json");
            var seen = File.Exists(file) && File.ReadAllText(file).Contains("\"onboardingDone\": true", StringComparison.Ordinal);
            checks.Check("开始使用 hides the guide and saves that it was seen", pressed && done is not null && hidden && seen,
                $"{how}; {done ?? "no 'onboarding: done' line"}; hidden={hidden}; settings.json {(seen ? "has onboardingDone true" : File.Exists(file) ? "lacks onboardingDone true" : "missing")}");
        }
        finally
        {
            using (var quit = Process.Start(new ProcessStartInfo(exe, ["--quit"]) { UseShellExecute = false }))
            {
                quit?.WaitForExit(10000);
            }

            if (!app.WaitForExit(10000))
            {
                app.Kill();
            }

            var error = log.FindSinceStart(" ERROR ");
            checks.Check("first run logged no errors", error is null, error ?? "none");

            // The sample sits in the user's real Pictures folder (a VM's may be the host's, through shared
            // folders), so leave nothing behind.
            try
            {
                if (Directory.Exists(samples))
                {
                    Directory.Delete(samples, recursive: true);
                }
            }
            catch (IOException ex)
            {
                Log.Info($"app: could not remove {samples}: {ex.Message}");
            }
        }
    }

    /// <returns>The sample photo, or null after a failed check.</returns>
    private static string? TrySample(Checks checks, AppLog log, string samples)
    {
        log.Mark();
        var pressed = Invoke(GuideTitle, "试一试", out var how);
        var at = log.WaitFor(5000, "onboarding: sample at ");
        var sample = at?[(at.IndexOf("sample at ", StringComparison.Ordinal) + "sample at ".Length)..];
        var shown = log.WaitFor(5000, "onboarding: showed the sample");
        var window = shown is null ? default : ExplorerSource.FolderWindow(samples, 10000);
        var item = $"no Explorer window titled '{Path.GetFileName(samples)}'";
        var selected = !window.IsNull && sample is not null && ExplorerSource.IsSelected(window, sample, out item);
        var inPictures = sample is not null && File.Exists(sample) && SameDir(Path.GetDirectoryName(sample)!, samples);
        checks.Check("试一试 puts a sample photo in Pictures and selects it in Explorer", pressed && inPictures && selected,
            $"{how}; {at ?? log.Find("onboarding: could not") ?? "no 'sample at' line"}; {shown ?? "no 'showed the sample' line"}; {item}");

        // The drag opens its own Explorer window on this folder; two would confuse the item lookup.
        ExplorerSource.CloseAndWait(window);
        return inPictures ? sample : null;
    }
}
