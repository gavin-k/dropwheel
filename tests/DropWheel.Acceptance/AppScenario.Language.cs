using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    /// <summary>The task window's title in English; it must match Strings.en.json in DropWheel.App.</summary>
    private const string EnglishQueueTitle = "DropWheel Tasks";

    /// <summary>
    /// With English picked in settings.json, DropWheel speaks English from its start even on a Chinese Windows:
    /// the log says so, and launching it again shows the task window under its English title, with a Settings
    /// button.
    /// </summary>
    private static void InEnglish(Checks checks, string exe)
    {
        var profile = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "english");
        if (Directory.Exists(profile))
        {
            Directory.Delete(profile, recursive: true);
        }

        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), """{ "version": 2, "onboardingDone": true, "language": "English" }""");
        var log = new AppLog();
        using var app = Process.Start(new ProcessStartInfo(exe, ["--data-dir", profile]) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })
            ?? throw new InvalidOperationException($"could not start {exe}");
        try
        {
            var ready = log.WaitFor(30000, "ready:");
            var language = log.FindSinceStart("language: ");
            if (!checks.Check("with English picked, DropWheel starts in English", ready is not null && language?.Contains("language: English (setting English, ", StringComparison.Ordinal) == true,
                $"{(ready is null ? "no 'ready:' line within 30 s" : language ?? "no 'language:' line")}"))
            {
                return;
            }

            // Launching it again shows the task window, as the tray icon would.
            using (var again = Process.Start(new ProcessStartInfo(exe, ["--data-dir", profile]) { UseShellExecute = false }))
            {
                again?.WaitForExit(10000);
            }

            var window = default(HWND);
            var shown = Checks.Wait(() => !(window = PInvoke.FindWindow(null, EnglishQueueTitle)).IsNull && PInvoke.IsWindowVisible(window), 5000);
            var how = $"no visible '{EnglishQueueTitle}'";
            var settings = shown ? Element(EnglishQueueTitle, "Settings", out how) : null;
            checks.Check("its task window is titled and labelled in English", shown && settings is not null, how);
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
            checks.Check("English start logged no errors", error is null, error ?? "none");
        }
    }
}
