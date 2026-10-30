using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    private const string SettingsTitle = "DropWheel 设置";

    /// <summary>
    /// Through the settings window, the way a screen reader would use it: the note that the wheel is solid is
    /// there exactly when its acrylic fell back; unticking the only key is refused
    /// with the reason and nothing is saved; Ctrl instead of Shift, touch long press off, the dock off, the
    /// output folder preset in settings.json and a file-name template are saved, and so are English and then
    /// 简体中文 picked as the language, the window staying Chinese; 隐私与离线 counts no network
    /// request, opens the log folder and clears the logs; 登录 Windows 时启动 writes and removes the Run value; a
    /// resting finger then opens nothing, nor does a drag pushed against the edge, and the next drag opens the
    /// wheel on Ctrl, not Shift, and puts the PNG in that folder, which does not exist yet, under the new name.
    /// </summary>
    private static void ChangeSettings(Checks checks, AppLog log, string profile, string folder, string exe, bool explorer)
    {
        var file = Path.Combine(profile, "settings.json");
        log.Mark();
        var opened = Invoke(QueueTitle, "设置", out var how);
        var window = default(HWND);
        opened = opened && Checks.Wait(() => !(window = PInvoke.FindWindow(null, SettingsTitle)).IsNull && PInvoke.IsWindowVisible(window), 5000);
        if (!checks.Check("设置 in the task window opens the settings window", opened, opened ? how : $"{how}; no visible '{SettingsTitle}'"))
        {
            return;
        }

        // Both test machines type Chinese and English only, so the warning must stay away; the refusal below
        // is matched in full and would fail if it carried the AltGr warning too.
        var layouts = log.WaitFor(2000, "keyboard layout has AltGr");
        checks.Check("the settings window reads the installed keyboard layouts for AltGr", layouts is not null, layouts ?? "no keyboard layout line");

        // The Win10 test machine has transparency effects off and its wheel's acrylic falls back to a solid
        // colour; the VM has them on. The note must be there exactly where the fallback happened.
        var solid = log.FindSinceStart("acrylic connected, state Fallback") is not null;
        var effects = log.WaitFor(2000, "settings: transparency effects");
        var note = Element(SettingsTitle, "solid-wheel", out _, byId: true) is not null;
        checks.Check("the settings window says the wheel is solid exactly when its acrylic fell back",
            effects is not null && note == solid && effects.Contains("effects off", StringComparison.Ordinal) == solid,
            $"acrylic {(solid ? "Fallback" : "Active")}; {effects ?? "no transparency line"}; note {(note ? "shown" : "not shown")}");

        var saved = File.ReadAllText(file);
        Toggle("转换 Shift");
        var refusal = Element(SettingsTitle, "这组按键没有保存 必须至少选择一个修饰键。", out how);
        var notSaved = log.WaitFor(2000, "settings: convert keys '' not saved");
        checks.Check("unticking the only key says why and saves nothing", refusal is not null && notSaved is not null && File.ReadAllText(file) == saved,
            $"{how}; {notSaved ?? "no 'not saved' line"}; settings.json {(File.ReadAllText(file) == saved ? "unchanged" : "changed")}");

        Toggle("转换 Ctrl");
        var keys = log.WaitFor(2000, "settings: saved convert keys Ctrl");
        Pattern<IUIAutomationTogglePattern>("touch-long-press", UIA_PATTERN_ID.UIA_TogglePatternId, byId: true)?.Toggle();
        var touch = log.WaitFor(2000, "settings: saved touch long press off");
        Pattern<IUIAutomationTogglePattern>("edge-dock", UIA_PATTERN_ID.UIA_TogglePatternId, byId: true)?.Toggle();
        var dock = log.WaitFor(2000, "settings: saved edge dock off");
        Pattern<IUIAutomationSelectionItemPattern>("统一放到一个文件夹", UIA_PATTERN_ID.UIA_SelectionItemPatternId)?.Select();
        var location = log.WaitFor(2000, $"settings: saved output folder {folder}");
        SetText("naming", "{name}-dw");
        var naming = log.WaitFor(3000, "settings: saved naming {name}-dw");
        checks.Check("Ctrl, long press off, the dock off, the output folder and the file name are saved as they are changed",
            keys is not null && touch is not null && dock is not null && location is not null && naming is not null,
            $"{keys ?? "no keys line"}; {touch ?? "no long press line"}; {dock ?? "no dock line"}; {location ?? "no folder line"}; {naming ?? "no naming line"}");

        // A language takes effect at the next start, so the window stays Chinese; the English scenario starts in English.
        var english = Pick("language", "English", out how) ? log.WaitFor(2000, "settings: saved language English") : null;
        var chinese = english is not null && Pick("language", "简体中文", out how) ? log.WaitFor(2000, "settings: saved language Chinese") : null;
        var still = Element(SettingsTitle, "唤出轮盘", out _) is not null;
        checks.Check("English and then 简体中文 are saved as they are picked, and the window stays Chinese",
            english is not null && chinese is not null && still,
            $"{how}; {english ?? "no English line"}; {chinese ?? "no Chinese line"}; 唤出轮盘 {(still ? "still there" : "gone")}");

        EditLayout(checks, log, profile);

        Privacy(checks, log);
        StartAtSignIn(checks, log, exe);

        PInvoke.PostMessage(window, PInvoke.WM_CLOSE, new WPARAM(0), new LPARAM(0));
        var hidden = Checks.Wait(() => !PInvoke.IsWindowVisible(window), 3000);
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var convert = json.RootElement.GetProperty("keys").GetProperty("bindings").EnumerateArray()
            .FirstOrDefault(b => b.GetProperty("mode").GetString() == "Convert");
        var output = json.RootElement.GetProperty("output");
        string Flag(string name) => json.RootElement.GetProperty("keys").TryGetProperty(name, out var on) ? (on.GetBoolean() ? "on" : "off") : "missing";
        var onDisk = $"convert {(convert.ValueKind == JsonValueKind.Object ? convert.GetProperty("keys").GetString() : "missing")}, long press {Flag("touchLongPress")}, "
            + $"dock {Flag("edgeDock")}, location {output.GetProperty("location").GetString()}, naming {output.GetProperty("naming").GetString()}";
        checks.Check("closing the settings window hides it, and settings.json holds the changes",
            hidden && onDisk == "convert Ctrl, long press off, dock off, location Folder, naming {name}-dw",
            $"visible={(bool)PInvoke.IsWindowVisible(window)}; {onDisk}");

        var dir = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "settings");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir))
        {
            File.Delete(old);
        }

        var jpg = Path.Combine(dir, $"m1s-{DateTime.Now:HHmmss}.jpg");
        WriteJpeg(jpg, 320, 200);
        RestOpensNothing(checks, log, jpg, explorer, Input.Touch, "with long press off a finger resting mid-drag opens nothing");
        EdgeOpensNothing(checks, log, jpg, explorer);
        var png = ConvertThroughWheel(checks, log, jpg, "png", explorer, [VIRTUAL_KEY.VK_LCONTROL], quiet: VIRTUAL_KEY.VK_LSHIFT);
        var expected = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(jpg)}-dw.png");
        checks.Check("the PNG goes to the new output folder under the new name", png is not null && string.Equals(png, expected, StringComparison.OrdinalIgnoreCase),
            $"{png ?? "no output"}, expected {expected}");
    }

    /// <summary>
    /// 隐私与离线: the count of network requests since start is 0, 打开日志文件夹 shows the folder in Explorer,
    /// and 清除日志 leaves only the log the app goes on writing.
    /// </summary>
    private static void Privacy(Checks checks, AppLog log)
    {
        var none = Element(SettingsTitle, "这次启动以来的网络请求：0 次", out var how);
        checks.Check("the settings window counts no network request since start", none is not null && log.FindSinceStart("network: ") is null,
            $"{how}; {log.FindSinceStart("network: ") ?? "no 'network:' line"}");

        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropWheel", "logs");
        var pressed = Invoke(SettingsTitle, "打开日志文件夹", out how);
        var folder = pressed ? ExplorerSource.FolderWindow(logs, 5000) : default;
        checks.Check("打开日志文件夹 shows the log folder in Explorer", !folder.IsNull, folder.IsNull ? $"{how}; no Explorer window on {logs}" : how);
        ExplorerSource.CloseAndWait(folder);

        var before = Directory.GetFiles(logs, "dropwheel-*.log").Length;
        var cleared = Invoke(SettingsTitle, "清除日志", out how) ? log.WaitFor(3000, "settings: cleared ") : null;
        var after = Directory.GetFiles(logs, "dropwheel-*.log").Select(Path.GetFileName).ToList();
        checks.Check("清除日志 deletes the logs and the app starts today's afresh",
            cleared is not null && after.Count == 1 && after[0] == $"dropwheel-{DateTime.Now:yyyyMMdd}.log" && new FileInfo(Path.Combine(logs, after[0])).Length < 4096,
            $"{cleared ?? how}; {before} log(s) before, after: {string.Join(", ", after)}");
    }

    /// <summary>
    /// 登录 Windows 时启动, which without a package is a value under the user's Run key: switching it on writes
    /// one that starts this DropWheel.exe with --at-sign-in, switching it off removes it. A value that was there
    /// before is put back.
    /// </summary>
    private static void StartAtSignIn(Checks checks, AppLog log, string exe)
    {
        if (Packaged)
        {
            StartupTaskAtSignIn(checks, log);
            return;
        }

        const string Name = "DropWheel";
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var before = run.GetValue(Name);
        try
        {
            string Switch(string what)
            {
                log.Mark();
                Pattern<IUIAutomationTogglePattern>("start-at-sign-in", UIA_PATTERN_ID.UIA_TogglePatternId, byId: true)?.Toggle();
                return log.WaitFor(2000, $"settings: sign-in run value {what}") ?? $"no 'sign-in run value {what}' line";
            }

            if (before is not null)
            {
                Switch("removed");
            }

            var on = Switch("set");
            var written = run.GetValue(Name) as string;
            var off = Switch("removed");
            var gone = run.GetValue(Name) is null;
            var expected = $"\"{exe}\" --at-sign-in";
            checks.Check("登录 Windows 时启动 writes the Run value that starts this DropWheel.exe in the tray, and removes it again",
                string.Equals(written, expected, StringComparison.OrdinalIgnoreCase) && gone,
                $"{on}; value [{written ?? "none"}], expected [{expected}]; {off}; {(gone ? "value gone" : $"value still [{run.GetValue(Name)}]")}");
        }
        finally
        {
            if (before is not null)
            {
                run.SetValue(Name, before);
            }
            else
            {
                run.DeleteValue(Name, throwOnMissingValue: false);
            }
        }
    }

    /// <summary>
    /// With a package the switch drives the manifest's startup task (Enabled="true" in Package.appxmanifest, so
    /// a fresh install starts with it on): each flip logs the task's new state, and the Run key is left alone.
    /// Flipped twice, so the task ends as it started.
    /// </summary>
    private static void StartupTaskAtSignIn(Checks checks, AppLog log)
    {
        const string Name = "DropWheel";
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var before = run?.GetValue(Name) as string;

        string Flip()
        {
            log.Mark();
            var toggle = Pattern<IUIAutomationTogglePattern>("start-at-sign-in", UIA_PATTERN_ID.UIA_TogglePatternId, byId: true);
            if (toggle is null)
            {
                return "no start-at-sign-in switch";
            }

            var expected = toggle.CurrentToggleState == ToggleState.ToggleState_On ? "Disabled" : "Enabled";
            toggle.Toggle();
            return log.WaitFor(5000, $"settings: startup task {expected}") ?? $"no 'settings: startup task {expected}' line";
        }

        var first = Flip();
        var second = Flip();
        var after = run?.GetValue(Name) as string;
        checks.Check("登录 Windows 时启动 flips the package's startup task off and on again, and leaves the Run key alone",
            !first.StartsWith("no ", StringComparison.Ordinal) && !second.StartsWith("no ", StringComparison.Ordinal) && string.Equals(before, after, StringComparison.Ordinal),
            $"{first}; {second}; Run value [{after ?? "none"}], before [{before ?? "none"}]");
    }

    private static void Toggle(string name) => Pattern<IUIAutomationTogglePattern>(name, UIA_PATTERN_ID.UIA_TogglePatternId)?.Toggle();

    private static unsafe void SetText(string id, string text)
    {
        var bstr = Marshal.StringToBSTR(text);
        try
        {
            Pattern<IUIAutomationValuePattern>(id, UIA_PATTERN_ID.UIA_ValuePatternId, byId: true)?.SetValue(new BSTR((char*)bstr));
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    /// <summary>
    /// 轮盘排布 in the settings window, which opens on the image tools wheel: 下移 on the first sector swaps it with
    /// the second in layout.json at once, and 下移 on the first sector again puts the wheel back as it was, so
    /// the scenarios after this one find the 示例 group where they left it.
    /// </summary>
    private static void EditLayout(Checks checks, AppLog log, string profile)
    {
        var file = Path.Combine(profile, "layout.json");
        var before = ImageTools(file);
        log.Mark();
        var moved = Invoke(SettingsTitle, "下移", out var how);
        var first = moved ? log.WaitFor(3000, "layout: changed") : null;
        var swapped = first is null ? null : ImageTools(file);
        var ok = before is { Count: >= 2 } && swapped is not null && swapped.Count == before.Count
            && swapped[0] == before[1] && swapped[1] == before[0] && swapped.Skip(2).SequenceEqual(before.Skip(2));
        checks.Check("下移 on the first sector of 轮盘排布 swaps the first two image tools in layout.json",
            ok, $"{how}; {first ?? "no 'layout: changed' line"}; before [{Join(before)}]; after [{Join(swapped)}]");

        log.Mark();
        var back = Invoke(SettingsTitle, "下移", out how) ? log.WaitFor(3000, "layout: changed") : null;
        var restored = back is null ? null : ImageTools(file);
        checks.Check("下移 on the first sector again puts the image tools back as they were",
            restored is not null && before is not null && restored.SequenceEqual(before),
            $"{how}; {back ?? "no second 'layout: changed' line"}; now [{Join(restored)}]");

        static string Join(List<string>? slots) => slots is null ? "unreadable" : string.Join(", ", slots);
    }

    /// <summary>The image tools wheel's six slots as they are written in layout.json, item ids or group names, or null when unreadable.</summary>
    private static List<string>? ImageTools(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var slots = doc.RootElement.GetProperty("wheels").GetProperty("tools").GetProperty("image");
            return [.. slots.EnumerateArray().Select(slot =>
                slot.ValueKind == JsonValueKind.Object && slot.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()!
                : slot.ValueKind == JsonValueKind.Object && slot.TryGetProperty("group", out var group) && group.ValueKind == JsonValueKind.Object ? "group:" + group.GetProperty("name").GetProperty("zh").GetString()
                : "empty")];
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Info($"settings: layout.json not readable: {ex.Message}");
            return null;
        }
    }

    /// <summary>A pattern of the element named <paramref name="name"/> (or with that id) in the settings window; null and logged when missing.</summary>
    private static T? Pattern<T>(string name, UIA_PATTERN_ID pattern, bool byId = false)
        where T : class
    {
        if (Element(SettingsTitle, name, out var how, byId) is not { } element)
        {
            Log.Info($"settings: {how}");
            return null;
        }

        return (T)element.GetCurrentPattern(pattern);
    }
}
