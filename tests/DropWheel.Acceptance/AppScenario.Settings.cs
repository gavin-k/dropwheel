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
    /// Through the settings window, the way a screen reader would use it: unticking the only key is refused
    /// with the reason and nothing is saved; Ctrl instead of Shift, the output folder preset in settings.json
    /// and a file-name template are saved, and the next drag opens the wheel on Ctrl, not Shift, and puts the
    /// PNG in that folder, which does not exist yet, under the new name.
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

        var saved = File.ReadAllText(file);
        Toggle("Shift");
        var refusal = Element(SettingsTitle, "这组按键没有保存 必须至少选择一个修饰键。", out how);
        var notSaved = log.WaitFor(2000, "settings: convert keys '' not saved");
        checks.Check("unticking the only key says why and saves nothing", refusal is not null && notSaved is not null && File.ReadAllText(file) == saved,
            $"{how}; {notSaved ?? "no 'not saved' line"}; settings.json {(File.ReadAllText(file) == saved ? "unchanged" : "changed")}");

        Toggle("Ctrl");
        var keys = log.WaitFor(2000, "settings: saved convert keys Ctrl");
        Pattern<IUIAutomationSelectionItemPattern>("统一放到一个文件夹", UIA_PATTERN_ID.UIA_SelectionItemPatternId)?.Select();
        var location = log.WaitFor(2000, $"settings: saved output folder {folder}");
        SetText("naming", "{name}-dw");
        var naming = log.WaitFor(3000, "settings: saved naming {name}-dw");
        checks.Check("Ctrl, the output folder and the file name are saved as they are changed", keys is not null && location is not null && naming is not null,
            $"{keys ?? "no keys line"}; {location ?? "no folder line"}; {naming ?? "no naming line"}");

        PInvoke.PostMessage(window, PInvoke.WM_CLOSE, new WPARAM(0), new LPARAM(0));
        var hidden = Checks.Wait(() => !PInvoke.IsWindowVisible(window), 3000);
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var convert = json.RootElement.GetProperty("keys").GetProperty("bindings").EnumerateArray()
            .FirstOrDefault(b => b.GetProperty("mode").GetString() == "Convert");
        var output = json.RootElement.GetProperty("output");
        var onDisk = $"convert {(convert.ValueKind == JsonValueKind.Object ? convert.GetProperty("keys").GetString() : "missing")}, "
            + $"location {output.GetProperty("location").GetString()}, naming {output.GetProperty("naming").GetString()}";
        checks.Check("closing the settings window hides it, and settings.json holds the changes", hidden && onDisk == "convert Ctrl, location Folder, naming {name}-dw",
            $"visible={(bool)PInvoke.IsWindowVisible(window)}; {onDisk}");

        var dir = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "settings");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir))
        {
            File.Delete(old);
        }

        var jpg = Path.Combine(dir, $"m1s-{DateTime.Now:HHmmss}.jpg");
        WriteJpeg(jpg, 320, 200);
        var png = ConvertThroughWheel(checks, log, jpg, "png", explorer, VIRTUAL_KEY.VK_LCONTROL, quiet: VIRTUAL_KEY.VK_LSHIFT);
        var expected = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(jpg)}-dw.png");
        checks.Check("the PNG goes to the new output folder under the new name", png is not null && string.Equals(png, expected, StringComparison.OrdinalIgnoreCase),
            $"{png ?? "no output"}, expected {expected}");
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
