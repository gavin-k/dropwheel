using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.Graphics;
using Windows.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AcrylicSpike;

/// <summary>
/// Creates the overlay window once (as M0 will: pre-created, hidden), then shows/hides it N times and
/// measures, per cycle: Show → first XAML frame (CompositionTarget.Rendered) and Show → pixels actually
/// changed on screen (BitBlt polling), plus whether the foreground window changed and what the acrylic
/// controller reports.  Writes a log and two pairs of BMP screenshots next to it.
/// </summary>
internal sealed class Runner(Options options)
{
    private const int MarginPx = 40;
    private const int ProbePx = 200;
    private const double OnScreenDiff = 0.02;

    private Window? _window;
    private AppWindow? _appWindow;
    private HWND _hwnd;
    private AcrylicDiscBackdrop? _disc;
    private TaskCompletionSource<double>? _renderedTcs;

    public async Task RunAsync()
    {
        var code = 2;
        try
        {
            code = await RunCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Info($"EXCEPTION {ex}");
            Log.Info("RESULT: FAIL");
        }

        Log.Info($"exit code {code}; log file: {Log.FilePath}");
        Environment.Exit(code);
    }

    private async Task<int> RunCoreAsync()
    {
        LogEnvironment();
        Log.Info($"options: variant={options.Variant} runs={options.Runs} size={options.SizePx}px defaultConfig={options.DefaultConfig} idleMs={options.IdleMs}");

        // ---- a busy scene behind the overlay, so blur (or its absence) is visible in the screenshots -----
        var scene = new Window();
        var sceneWin = scene.AppWindow;
        var scenePresenter = OverlappedPresenter.Create();
        scenePresenter.SetBorderAndTitleBar(false, false);
        sceneWin.SetPresenter(scenePresenter);
        scene.Content = SceneContent();
        var work0 = DisplayArea.Primary.WorkArea;
        var scenePx = options.SizePx + 2 * MarginPx + 160;
        sceneWin.MoveAndResize(new RectInt32(work0.X + (work0.Width - scenePx) / 2, work0.Y + (work0.Height - scenePx) / 2, scenePx, scenePx));
        scene.Activate();
        await Task.Delay(800);
        Log.Info($"scene window shown and activated: fg=0x{Hex(PInvoke.GetForegroundWindow())}");

        // ---- window, created once ---------------------------------------------------------------
        var tCreate = Log.NowMs;
        _window = new Window();
        _hwnd = ToHwnd(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        var dpi = PInvoke.GetDpiForWindow(_hwnd);
        var scale = dpi / 96.0;
        var sizeDip = options.SizePx / scale;

        _appWindow = _window.AppWindow;
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        _appWindow.SetPresenter(presenter);
        _appWindow.IsShownInSwitchers = false;

        // The overlay must never take focus away from the drag source: no-activate tool window.
        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        exStyle |= WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST;
        PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)exStyle);

        var work = DisplayArea.Primary.WorkArea;
        var winPx = options.SizePx + 2 * MarginPx;
        var rect = new RectInt32(work.X + (work.Width - winPx) / 2, work.Y + (work.Height - winPx) / 2, winPx, winPx);
        _appWindow.MoveAndResize(rect);

        if (options.Variant != Variant.Opaque)
        {
            _window.SystemBackdrop = new TransparentBackdrop();
        }

        var root = new Grid
        {
            Background = options.Variant == Variant.Opaque ? new SolidColorBrush(Color.FromArgb(255, 32, 32, 32)) : null,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        FrameworkElement disc;
        if (options.Variant == Variant.None)
        {
            disc = new Border
            {
                Width = sizeDip,
                Height = sizeDip,
                CornerRadius = new CornerRadius(sizeDip / 2),
                Background = new SolidColorBrush(Color.FromArgb(128, 32, 32, 32)),
            };
        }
        else
        {
            _disc = new AcrylicDiscBackdrop(options.DefaultConfig);
            disc = new SystemBackdropElement
            {
                Width = sizeDip,
                Height = sizeDip,
                CornerRadius = new CornerRadius(sizeDip / 2),
                SystemBackdrop = _disc,
            };
        }

        disc.HorizontalAlignment = HorizontalAlignment.Center;
        disc.VerticalAlignment = VerticalAlignment.Center;
        root.Children.Add(disc);
        root.Children.Add(new TextBlock
        {
            Text = "DropWheel acrylic spike",
            Foreground = new SolidColorBrush(Colors.White),
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        });
        _window.Content = root;
        CompositionTarget.Rendered += (_, e) =>
        {
            var tcs = _renderedTcs;
            if (tcs is not null && tcs.TrySetResult(Log.NowMs))
            {
                Log.Info($"  CompositionTarget.Rendered (frame {e.FrameDuration.TotalMilliseconds:F1} ms)");
            }
        };
        Log.Info($"window created hidden in {Log.NowMs - tCreate:F0} ms: hwnd=0x{Hex(_hwnd)} rect={rect.X},{rect.Y} {rect.Width}x{rect.Height} dpi={dpi} disc={sizeDip:F0} dip exstyle=0x{(uint)exStyle:X}");

        // ---- probes --------------------------------------------------------------------------------
        using var center = new ScreenProbe(rect.X + (winPx - ProbePx) / 2, rect.Y + (winPx - ProbePx) / 2, ProbePx, ProbePx);
        using var full = new ScreenProbe(rect.X, rect.Y, winPx, winPx);
        var shotDir = Path.GetDirectoryName(Log.FilePath!)!;
        var shotPrefix = Path.GetFileNameWithoutExtension(Log.FilePath!);

        // ---- cycles ---------------------------------------------------------------------------------
        var rendered = new List<double>();
        var onScreen = new List<double>();
        var fgChanged = 0;
        var missed = 0;
        var states = new List<string>();
        var ownCpu = Process.GetCurrentProcess();
        var dwm = TryDwm();
        try
        {
            Log.Info(dwm is null ? "dwm.exe not found; dwm CPU column will read 0" : $"dwm.exe pid {dwm.Id}, CPU so far {dwm.TotalProcessorTime.TotalMilliseconds:F0} ms (readable)");
        }
        catch (Exception ex)
        {
            Log.Info($"dwm.exe CPU not readable ({ex.GetType().Name}: {ex.Message}); dwm CPU column will read 0");
        }
        for (var i = 1; i <= options.Runs; i++)
        {
            var before = center.Capture();
            var fullBefore = i <= 2 ? full.Capture() : null;
            var fgBefore = PInvoke.GetForegroundWindow();
            ownCpu.Refresh();
            dwm?.Refresh();
            var ownBefore = ownCpu.TotalProcessorTime;
            var dwmBefore = SafeCpu(dwm);

            _renderedTcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var t0 = Log.NowMs;
            var pixels = Task.Run(() =>
            {
                var deadline = t0 + 2000;
                while (Log.NowMs < deadline)
                {
                    var now = center.Capture();
                    var t = Log.NowMs;
                    if (ScreenProbe.DiffFraction(before, now) > OnScreenDiff)
                    {
                        return t;
                    }

                    Thread.Sleep(3);
                }

                return double.NaN;
            });
            _appWindow.Show(false);
            var tShowReturned = Log.NowMs;

            var renderedAt = await Task.WhenAny(_renderedTcs.Task, Task.Delay(2000)) == _renderedTcs.Task ? _renderedTcs.Task.Result : double.NaN;
            var onScreenAt = await pixels;
            await Task.Delay(400);

            var fgAfter = PInvoke.GetForegroundWindow();
            ownCpu.Refresh();
            dwm?.Refresh();
            var ownMs = (ownCpu.TotalProcessorTime - ownBefore).TotalMilliseconds;
            var dwmMs = (SafeCpu(dwm) - dwmBefore).TotalMilliseconds;
            var state = _disc?.Controller?.State.ToString() ?? "n/a";
            states.Add(state);
            var settled = full.Capture();
            var (mean, sd, magenta, grad) = ScreenProbe.DiscStats(settled, winPx, winPx, options.SizePx / 2 - 8);
            if (fullBefore is not null)
            {
                ScreenProbe.SaveBmp(Path.Combine(shotDir, $"{shotPrefix}-cycle{i}-before.bmp"), fullBefore, winPx, winPx);
                ScreenProbe.SaveBmp(Path.Combine(shotDir, $"{shotPrefix}-cycle{i}-after.bmp"), settled, winPx, winPx);
            }

            var sameFg = fgBefore == fgAfter;
            if (!sameFg) { fgChanged++; }
            if (double.IsNaN(onScreenAt)) { missed++; }
            if (!double.IsNaN(renderedAt)) { rendered.Add(renderedAt - t0); }
            if (!double.IsNaN(onScreenAt)) { onScreen.Add(onScreenAt - t0); }
            Log.Info($"cycle {i,2}: Show() returned +{tShowReturned - t0:F1} ms, Rendered {Fmt(renderedAt, t0)}, on-screen {Fmt(onScreenAt, t0)}, acrylic={state}, fg {(sameFg ? "unchanged" : $"CHANGED 0x{Hex(fgBefore)}->0x{Hex(fgAfter)}")}, disc grey mean={mean:F0} sd={sd:F1} edge|∇|={grad:F2} magenta px={magenta}, cpu(own)={ownMs:F0} ms cpu(dwm)={dwmMs:F0} ms over ~{Log.NowMs - t0:F0} ms wall");

            _appWindow.Hide();
            await Task.Delay(300);
            if (i == 1 && options.IdleMs > 0)
            {
                Log.Info($"idle: staying hidden for {options.IdleMs} ms before cycle 2");
                await Task.Delay(options.IdleMs);
            }
        }

        // ---- summary --------------------------------------------------------------------------------
        Log.Info("");
        Log.Info($"==== {options.Variant}{(options.DefaultConfig ? " (default config)" : "")}: {options.Runs} show/hide cycles, {options.SizePx} px disc ====");
        Summarize("Show→Rendered ", rendered);
        Summarize("Show→on-screen", onScreen);
        if (options.IdleMs > 0 && missed == 0 && onScreen.Count >= 2)
        {
            Log.Info($"  Show→on-screen after {options.IdleMs} ms idle (cycle 2): {onScreen[1]:F0} ms; cold cycle 1 {onScreen[0]:F0} ms; warm cycles 3..{options.Runs} median {(onScreen.Count > 2 ? Median(onScreen.Skip(2).ToList()).ToString("F0") : "n/a")} ms");
        }
        Log.Info($"  foreground changed in {fgChanged} cycle(s); on-screen detection missed in {missed} cycle(s)");
        Log.Info($"  acrylic controller state per cycle: {string.Join(",", states.Distinct())}{(options.Variant == Variant.None ? "" : $" (IsSupported={DesktopAcrylicController.IsSupported()})")}");
        var warm = onScreen.Skip(1).ToList();
        var warmMedian = warm.Count > 0 ? Median(warm) : double.NaN;
        var pass = fgChanged == 0 && missed == 0 && warm.Count > 0 && warmMedian <= 150;
        Log.Info($"ACRYLIC: {(options.Variant == Variant.None ? "n/a" : _disc?.Controller?.State.ToString() ?? "n/a")}");
        Log.Info(pass ? "RESULT: PASS" : "RESULT: FAIL");
        return pass ? 0 : 1;
    }

    /// <summary>4×4 checkerboard of saturated colours with text: hard edges that acrylic should blur and a plain overlay should not.</summary>
    private static UIElement SceneContent()
    {
        var grid = new Grid();
        Color[] palette = [Color.FromArgb(255, 230, 60, 60), Color.FromArgb(255, 60, 200, 80), Color.FromArgb(255, 60, 110, 230), Color.FromArgb(255, 240, 200, 40), Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 20, 20, 20)];
        for (var i = 0; i < 6; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        for (var r = 0; r < 6; r++)
        {
            for (var c = 0; c < 6; c++)
            {
                var cell = new Border { Background = new SolidColorBrush(palette[(r * 7 + c) % palette.Length]) };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
                var text = new TextBlock { Text = $"{r}{c}", FontSize = 28, Foreground = new SolidColorBrush(Colors.Black), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(text, r);
                Grid.SetColumn(text, c);
                grid.Children.Add(text);
            }
        }

        return grid;
    }

    private static void Summarize(string label, List<double> values)
    {
        if (values.Count == 0)
        {
            Log.Info($"  {label}: no data");
            return;
        }

        var warm = values.Skip(1).ToList();
        var warmText = warm.Count > 0 ? $"warm (cycles 2..{values.Count}) min {warm.Min():F0} / median {Median(warm):F0} / max {warm.Max():F0} ms" : "no warm cycles";
        Log.Info($"  {label}: cold cycle 1 {values[0]:F0} ms; {warmText}");
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static unsafe HWND ToHwnd(nint handle) => new((void*)handle);

    private static unsafe string Hex(HWND h) => ((nint)h.Value).ToString("X");

    private static string Fmt(double t, double t0) => double.IsNaN(t) ? "none within 2 s" : $"+{t - t0:F1} ms";

    private static Process? TryDwm()
    {
        try { return Process.GetProcessesByName("dwm").FirstOrDefault(); } catch { return null; }
    }

    private static TimeSpan SafeCpu(Process? p)
    {
        try { return p?.TotalProcessorTime ?? TimeSpan.Zero; } catch { return TimeSpan.Zero; }
    }

    private static unsafe void LogEnvironment()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        Log.Info($"AcrylicSpike on Windows {key?.GetValue("DisplayVersion")} build {key?.GetValue("CurrentBuild")}.{key?.GetValue("UBR")}, {RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");
        using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var power = new SYSTEM_POWER_STATUS();
        var powerOk = PInvoke.GetSystemPowerStatus(&power);
        string? tools = null;
        try
        {
            using var prl = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Parallels\Tools");
            tools = prl?.GetValue("Version")?.ToString();
        }
        catch { }

        Log.Info($"DesktopAcrylicController.IsSupported={DesktopAcrylicController.IsSupported()}, EnableTransparency={personalize?.GetValue("EnableTransparency") ?? "(unset)"}, battery saver={(powerOk ? (power.SystemStatusFlag == 1).ToString() : "unknown")}, AC={(powerOk ? power.ACLineStatus.ToString() : "unknown")}, Parallels Tools={tools ?? "not found"}");
    }
}
