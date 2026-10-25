using System.Runtime.InteropServices;
using System.Text.Json;
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
    /// output folder preset in settings.json and a file-name template are saved; 隐私与离线 counts no network
    /// request, opens the log folder and clears the logs; a resting finger then opens nothing, nor does a
    /// drag pushed against the edge, and the next drag opens the wheel on Ctrl, not Shift, and puts the PNG
    /// in that folder, which does not exist yet, under the new name.
    /// </summary>
    private static void ChangeSettings(Checks checks, AppLog log, string profile, string folder, bool explorer)
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

        Privacy(checks, log);

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
