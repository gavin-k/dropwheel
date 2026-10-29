using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DropWheel.Core.Wheel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    private const string HelloId = "user.hello-pwsh";
    private const string SleeperId = "user.sleeper";

    /// <summary>pwsh processes a user tool started and left running; <see cref="Quit"/> checks they went with the app.</summary>
    private static readonly List<int> ToolChildren = [];

    /// <summary>
    /// User tools, the M1.5 ① flow: the shipped hello-pwsh example validates from the command line and a manifest
    /// without an id does not; copied into the profile's tools folder while the app runs it is loaded, placed in
    /// the 示例 group of the full image Tools wheel and greyed; resting on the group sector opens the ring, a
    /// release on the group sector runs nothing, moving to another sector closes the ring; switched on in
    /// 设置 › 我的工具 it copies a JPG next to the source through the ring and leaves no scratch folder; a change
    /// to its command greys it again; a broken folder gets a .problems.json and a note in settings; an edit to
    /// layout.json is picked up; and a tool still running when the app quits is ended with it.
    /// </summary>
    private static void UserTools(Checks checks, AppLog log, string exe, string profile, bool explorer)
    {
        var examples = Path.Combine(Path.GetDirectoryName(exe)!, "skills", "dropwheel-tool", "examples");
        var tools = Path.Combine(profile, "tools");
        var dir = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "usertools");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFileSystemEntries(dir))
        {
            if (Directory.Exists(old))
            {
                Directory.Delete(old, recursive: true);
            }
            else
            {
                File.Delete(old);
            }
        }

        // The command line first: the agent's self-check, from a console with redirected output.
        var hello = Path.Combine(examples, "hello-pwsh");
        var (code, json) = ToolCommand(exe, "--validate-tool", hello);
        checks.Check("DropWheel.exe --validate-tool accepts the shipped hello-pwsh example", code == 0 && Problems(json) == "", $"exit {code}; {Shorten(json)}");

        var broken = Path.Combine(dir, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "tool.json"), """{ "schema": 1, "name": "坏的", "wheel": "tools", "script": { "command": "pwsh", "args": [] } }""");
        (code, json) = ToolCommand(exe, "--validate-tool", broken);
        checks.Check("--validate-tool exits 1 and names the id problem for a manifest without one", code == 1 && Problems(json).Contains("id", StringComparison.Ordinal), $"exit {code}; {Shorten(json)}");

        // Then the folder itself, the way an agent writes it, while the app is running.
        log.Mark();
        CopyTree(hello, Path.Combine(tools, "hello-pwsh"));
        var loaded = log.WaitFor(5000, "tools: 1 loaded", HelloId);
        var placed = loaded is null ? null : log.WaitFor(2000, $"layout: {HelloId} placed");
        checks.Check("a tool folder dropped into the tools folder is loaded and placed while the app runs", loaded is not null && placed is not null,
            $"{loaded ?? "no 'tools: 1 loaded' line within 5 s"}; {placed ?? "no 'layout: placed' line"}");
        if (loaded is null)
        {
            return;
        }

        (code, json) = ToolCommand(exe, "--list-tools", "--data-dir", profile);
        checks.Check("--list-tools lists it", code == 0 && json.Contains($"\"id\":\"{HelloId}\"", StringComparison.Ordinal), $"exit {code}; {Shorten(json)}");

        // Greyed until switched on: the ring opens on a rest, shows the child off, and a release on the group runs nothing.
        var jpg = Path.Combine(dir, $"ut-{DateTime.Now:HHmmss}.jpg");
        WriteJpeg(jpg, 640, 400);
        using (var r1 = OpenRing(checks, log, jpg, explorer, "before it is switched on"))
        {
            if (r1 is not null)
            {
                checks.Check("the new tool is in the ring and off until it is switched on", r1.Children.Contains($"{HelloId}(off)"), r1.Line);
                Input.LeftUp();
                var enqueued = log.WaitFor(1500, "wheel: enqueued");
                var closed = log.WaitFor(3000, "wheel: closed");
                checks.Check("a release on the group sector runs nothing and closes the wheel", enqueued is null && closed is not null, $"{enqueued ?? "nothing enqueued"}; {closed ?? "wheel never closed"}");
            }
        }

        // Moving to another top-level sector closes the ring; a return to the hub is a cancel.
        using (var r2 = OpenRing(checks, log, jpg, explorer, "a second time"))
        {
            if (r2 is not null)
            {
                log.Mark();
                var other = (r2.Parent + 1) % WheelModel.SlotCount;
                Input.MoveSmooth(SectorPoint(r2.Wheel, r2.Parent), SectorPoint(r2.Wheel, other), 6, 30);
                var closedRing = log.WaitFor(2000, "wheel: ring closed");
                checks.Check("moving to another sector closes the ring", closedRing is not null, closedRing ?? "no 'ring closed' line within 2 s");
                Input.MoveSmooth(SectorPoint(r2.Wheel, other), new Point(r2.Wheel.X, r2.Wheel.Y), 6, 30);
                Thread.Sleep(200);
                Input.LeftUp();
                log.WaitFor(3000, "wheel: closed");
            }
        }

        // Switch it on in settings, which is where the trust lives.
        var window = OpenSettings(checks, log);
        if (window.IsNull)
        {
            return;
        }

        log.Mark();
        Pattern<IUIAutomationTogglePattern>("tool-" + HelloId, UIA_PATTERN_ID.UIA_TogglePatternId, byId: true)?.Toggle();
        var allowed = log.WaitFor(3000, $"settings: saved tool {HelloId} allowed");
        checks.Check("允许运行 in 我的工具 saves the tool as trusted", allowed is not null, allowed ?? "no 'saved tool ... allowed' line within 3 s");

        // A folder with a broken manifest: a .problems.json for the agent, a note for the user.
        log.Mark();
        CopyTree(broken, Path.Combine(tools, "broken"));
        var problem = log.WaitFor(5000, "tools: broken: error id:");
        var problems = Path.Combine(tools, "broken", ".problems.json");
        var written = Checks.Wait(() => File.Exists(problems), 3000);
        var note = Element(SettingsTitle, "broken 没有加载", out var how) is not null;
        checks.Check("a broken manifest gets a .problems.json next to it and a note in 我的工具", problem is not null && written && note,
            $"{problem ?? "no 'tools: broken: error id' line within 5 s"}; .problems.json {(written ? "written" : "missing")}; {how}");
        Directory.Delete(Path.Combine(tools, "broken"), recursive: true);
        log.WaitFor(5000, "tools: 1 loaded");

        PInvoke.PostMessage(window, PInvoke.WM_CLOSE, new WPARAM(0), new LPARAM(0));
        Checks.Wait(() => !PInvoke.IsWindowVisible(window), 3000);

        // Now it runs: through the ring, a copy lands next to the source and no scratch folder is left behind.
        using (var r3 = OpenRing(checks, log, jpg, explorer, "once switched on"))
        {
            var child = r3 is null ? -1 : ChildIndex(r3, HelloId);
            checks.Check("once switched on the tool is live in the ring", r3 is not null && child >= 0 && !r3.Children.Contains($"{HelloId}(off)"), r3?.Line ?? "no ring");
            if (r3 is not null && child >= 0)
            {
                log.Mark();
                Input.MoveSmooth(SectorPoint(r3.Wheel, r3.Parent), ChildPoint(r3, child), 6, 30);
                Thread.Sleep(250);
                Input.LeftUp();
                var enqueued = log.WaitFor(3000, "wheel: enqueued ", "复制一份");
                var label = enqueued is null ? null : enqueued[(enqueued.IndexOf("wheel: enqueued ", StringComparison.Ordinal) + "wheel: enqueued ".Length)..];
                var done = label is null ? null : log.WaitFor(60000, $"queue: {label} ");
                var output = done is null ? null : OutputsRegex().Match(done) is { Success: true } m ? m.Groups[1].Value : null;
                var expected = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(jpg)}-copy.jpg");
                var scratch = Directory.GetFileSystemEntries(dir, "*.dropwheel-tmp");
                checks.Check("复制一份 through the ring copies the JPG next to the source and leaves no scratch folder",
                    string.Equals(output, expected, StringComparison.OrdinalIgnoreCase) && File.Exists(expected) && new FileInfo(expected).Length == new FileInfo(jpg).Length && scratch.Length == 0,
                    $"{enqueued ?? "nothing enqueued within 3 s"}; {done ?? "no queue outcome within 60 s"}; scratch [{string.Join(", ", scratch.Select(f => Path.GetFileName(f)))}]");
            }
            else if (r3 is not null)
            {
                Input.LeftUp();
                log.WaitFor(3000, "wheel: closed");
            }
        }

        // layout.json is the agent's too: an outside edit is noticed.
        log.Mark();
        var layout = Path.Combine(profile, "layout.json");
        File.WriteAllText(layout, File.ReadAllText(layout) + Environment.NewLine);
        var changed = log.WaitFor(5000, "layout: changed");
        checks.Check("an edit to layout.json while the app runs is picked up", changed is not null, changed ?? "no 'layout: changed' line within 5 s");

        // A changed command is a different script: trust is withdrawn until the user looks again.
        log.Mark();
        var manifest = Path.Combine(tools, "hello-pwsh", "tool.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"-NoProfile\"", "\"-NoLogo\", \"-NoProfile\"", StringComparison.Ordinal));
        var reloaded = log.WaitFor(5000, "tools: 1 loaded", HelloId);
        using (var r4 = reloaded is null ? null : OpenRing(checks, log, jpg, explorer, "after its command changed"))
        {
            checks.Check("a change to the tool's command greys it again", r4 is not null && r4.Children.Contains($"{HelloId}(off)"),
                $"{reloaded ?? "no reload line within 5 s"}; {r4?.Line ?? "no ring"}");
            if (r4 is not null)
            {
                Input.LeftUp();
                log.WaitFor(3000, "wheel: closed");
            }
        }

        // A tool that keeps running: its pwsh must go when the app does (checked in Quit).
        var sleeper = Path.Combine(tools, "sleeper");
        Directory.CreateDirectory(sleeper);
        log.Mark();
        File.WriteAllText(Path.Combine(sleeper, "tool.json"), $$"""
            { "schema": 1, "id": "{{SleeperId}}", "name": { "zh": "慢工具", "en": "Slow tool" }, "wheel": "tools", "appliesTo": ["image"], "kind": "script",
              "script": { "command": "pwsh", "args": ["-NoProfile", "-Command", "Start-Sleep 3600"], "output": { "mode": "none" }, "timeoutSeconds": 7200 },
              "placement": { "group": { "zh": "示例", "en": "Examples" } }, "undo": "none" }
            """);
        var two = log.WaitFor(5000, "tools: 2 loaded", SleeperId);
        window = two is null ? default : OpenSettings(checks, log);
        if (!window.IsNull)
        {
            log.Mark();
            Pattern<IUIAutomationTogglePattern>("tool-" + SleeperId, UIA_PATTERN_ID.UIA_TogglePatternId, byId: true)?.Toggle();
            log.WaitFor(3000, $"settings: saved tool {SleeperId} allowed");
            PInvoke.PostMessage(window, PInvoke.WM_CLOSE, new WPARAM(0), new LPARAM(0));
            Checks.Wait(() => !PInvoke.IsWindowVisible(window), 3000);
        }

        var before = Process.GetProcessesByName("pwsh").Select(p => p.Id).ToHashSet();
        using (var r5 = two is null ? null : OpenRing(checks, log, jpg, explorer, "with the slow tool"))
        {
            var slow = r5 is null ? -1 : ChildIndex(r5, SleeperId);
            if (r5 is not null && slow >= 0)
            {
                log.Mark();
                Input.MoveSmooth(SectorPoint(r5.Wheel, r5.Parent), ChildPoint(r5, slow), 6, 30);
                Thread.Sleep(250);
                Input.LeftUp();
                var enqueued = log.WaitFor(3000, "wheel: enqueued ", "慢工具");
                var started = Checks.Wait(() => Process.GetProcessesByName("pwsh").Any(p => !before.Contains(p.Id)), 15000);
                ToolChildren.AddRange(Process.GetProcessesByName("pwsh").Select(p => p.Id).Where(id => !before.Contains(id)));
                checks.Check("a slow tool's pwsh is running once it is dropped on", enqueued is not null && started, $"{enqueued ?? "nothing enqueued"}; new pwsh pids [{string.Join(", ", ToolChildren)}]");
            }
            else
            {
                checks.Check("a slow tool's pwsh is running once it is dropped on", false, $"{two ?? "sleeper not loaded"}; {r5?.Line ?? "no ring"}");
                if (r5 is not null)
                {
                    Input.LeftUp();
                }
            }
        }
    }

    /// <summary>An open ring with the drag still held; disposing releases the button and closes the drag source.</summary>
    private sealed class Ring(DragSource drag, Wheel wheel, int parent, string line, string[] children) : IDisposable
    {
        public Wheel Wheel { get; } = wheel;

        public int Parent { get; } = parent;

        public string Line { get; } = line;

        public string[] Children { get; } = children;

        public void Dispose() => drag.Dispose();
    }

    /// <summary>Drags the JPG, taps Ctrl+Alt, rests on the 示例 group sector and waits for the ring to open; the button stays down.</summary>
    private static Ring? OpenRing(Checks checks, AppLog log, string jpg, bool explorer, string when)
    {
        var drag = DragSource.Open(jpg, explorer);
        var mid = drag.PressAndDrag();
        log.Mark();
        if (Tap(checks, log, mid, CtrlAlt) is not { } w)
        {
            drag.Dispose();
            return null;
        }

        var entered = log.WaitFor(2000, "file(s) entered");
        var sector = SlotOf(entered, "group:示例");
        if (!checks.Check($"the image Tools wheel shows the 示例 group {when}", sector >= 0, entered ?? "no 'file(s) entered' line within 2 s"))
        {
            Input.MoveSmooth(mid, new Point(w.X, w.Y), 4, 30);
            drag.Dispose();
            return null;
        }

        // A wheel sized for the ring may have been shifted away from the pointer, so go through the hub first.
        var hub = new Point(w.X, w.Y);
        Input.MoveSmooth(new Point(mid.X + 6, mid.Y + 6), hub, 6, 30);
        Thread.Sleep(100);
        Input.MoveSmooth(hub, SectorPoint(w, sector), 8, 30);
        var opened = log.WaitFor(1500, $"wheel: ring open on sector {sector}");
        if (!checks.Check($"resting on the group sector opens the ring {when}", opened is not null, opened ?? "no 'ring open' line within 1.5 s"))
        {
            drag.Dispose();
            return null;
        }

        var children = opened![(opened.IndexOf("): ", StringComparison.Ordinal) + 3)..].Split(',');
        return new Ring(drag, w, sector, opened, children);
    }

    private static int ChildIndex(Ring ring, string id)
    {
        foreach (var child in ring.Children)
        {
            var parts = child.Split('=');
            if (parts.Length == 2 && (parts[1] == id || parts[1] == id + "(off)"))
            {
                return int.Parse(parts[0], CultureInfo.InvariantCulture);
            }
        }

        return -1;
    }

    private static Point ChildPoint(Ring ring, int child)
    {
        var (dx, dy) = WheelGeometry.ChildCenter(ring.Parent, ring.Children.Length, child);
        return new Point(ring.Wheel.X + (int)Math.Round(dx * ring.Wheel.Scale), ring.Wheel.Y + (int)Math.Round(dy * ring.Wheel.Scale));
    }

    private static HWND OpenSettings(Checks checks, AppLog log)
    {
        var opened = Invoke(QueueTitle, "设置", out var how);
        var window = default(HWND);
        opened = opened && Checks.Wait(() => !(window = PInvoke.FindWindow(null, SettingsTitle)).IsNull && PInvoke.IsWindowVisible(window), 5000);
        checks.Check("设置 opens the settings window for 我的工具", opened, opened ? how : $"{how}; no visible '{SettingsTitle}'");
        return opened ? window : default;
    }

    /// <summary>Runs DropWheel.exe with a tool command line and returns its exit code and what it printed.</summary>
    private static (int Code, string Output) ToolCommand(string exe, params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 })
            ?? throw new InvalidOperationException($"could not start {exe}");
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill();
            return (-1, "timed out after 30 s");
        }

        return (process.ExitCode, output.Result.Trim());
    }

    /// <summary>The codes of the errors (not warnings) in a --validate-tool report, comma-separated; empty when none or unreadable.</summary>
    private static string Problems(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("problems", out var problems)
                ? string.Join(",", problems.EnumerateArray().Where(p => !(p.TryGetProperty("isWarning", out var w) && w.GetBoolean())).Select(p => p.GetProperty("code").GetString()))
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var sub in Directory.GetDirectories(from))
        {
            CopyTree(sub, Path.Combine(to, Path.GetFileName(sub)));
        }
    }
}
