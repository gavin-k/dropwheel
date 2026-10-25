using System.IO.Compression;
using System.Text;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    private static readonly VIRTUAL_KEY[] CtrlAlt = [VIRTUAL_KEY.VK_LCONTROL, VIRTUAL_KEY.VK_LMENU];

    /// <summary>
    /// The Tools wheel, opened by a Ctrl+Alt tap mid-drag: 缩小 halves a JPG into a copy named by its new
    /// size, and 解压 unpacks a zip into a folder named after it, which 撤销 then moves to the Recycle Bin.
    /// </summary>
    private static void Tools(Checks checks, AppLog log, bool explorer)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "tools");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir))
        {
            File.Delete(old);
        }

        foreach (var old in Directory.GetDirectories(dir))
        {
            Directory.Delete(old, recursive: true);
        }

        var stamp = DateTime.Now.ToString("HHmmss");
        HalfSize(checks, log, Path.Combine(dir, $"half-{stamp}.jpg"), explorer);
        Extract(checks, log, Path.Combine(dir, $"unzip-{stamp}.zip"), explorer);
    }

    private static void HalfSize(Checks checks, AppLog log, string jpg, bool explorer)
    {
        WriteJpeg(jpg, 640, 400);

        // Alt going up can open a menu bar in the window the drag came from; the drop must still land.
        var owner = "not looked at";
        if (DropOnWheel(checks, log, jpg, "tool.half-size", explorer, CtrlAlt, beforeRelease: () => owner = ForegroundOwner()) is not { } label)
        {
            return;
        }

        var done = log.WaitFor(30000, $"queue: {label} ");
        var output = done is null ? null : OutputsRegex().Match(done) is { Success: true } m ? m.Groups[1].Value : null;
        var expected = Path.Combine(Path.GetDirectoryName(jpg)!, $"{Path.GetFileNameWithoutExtension(jpg)}-320x200.jpg");
        var size = "no output";
        if (output is not null && File.Exists(output))
        {
            using var image = Image.FromFile(output);
            size = $"{image.Width}x{image.Height}";
        }

        checks.Check("缩小 halves the JPG into a copy named by its new size",
            string.Equals(output, expected, StringComparison.OrdinalIgnoreCase) && size == "320x200" && File.Exists(jpg),
            $"{done ?? "no queue outcome within 30 s"}; {size}; foreground before the drop {owner}");
    }

    private static void Extract(Checks checks, AppLog log, string zip, bool explorer)
    {
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("说明.txt", "说明"), ("图/a.txt", "a") })
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        if (DropOnWheel(checks, log, zip, "tool.extract", explorer, CtrlAlt) is not { } label)
        {
            return;
        }

        var done = log.WaitFor(30000, $"queue: {label} ");
        var output = done is null ? null : OutputsRegex().Match(done) is { Success: true } m ? m.Groups[1].Value : null;
        var expected = Path.Combine(Path.GetDirectoryName(zip)!, Path.GetFileNameWithoutExtension(zip));
        var files = output is not null && Directory.Exists(output)
            ? string.Join(", ", Directory.GetFiles(output, "*", SearchOption.AllDirectories)
                .Select(f => $"{Path.GetRelativePath(output, f).Replace('\\', '/')}={File.ReadAllText(f)}").Order(StringComparer.Ordinal))
            : "";
        var unpacked = string.Equals(output, expected, StringComparison.OrdinalIgnoreCase) && files == "图/a.txt=a, 说明.txt=说明";
        checks.Check("解压 unpacks the zip into a folder named after it", unpacked, $"{done ?? "no queue outcome within 30 s"}; files [{files}]");
        if (!unpacked)
        {
            return;
        }

        var pressed = PressUndo(label, out var how);
        var undone = pressed ? log.WaitFor(10000, $"queue: undid {label}") : null;
        Checks.Wait(() => !Directory.Exists(expected), 3000);
        var binned = FileOpsScenario.InRecycleBin(Path.GetDirectoryName(zip)!);
        checks.Check("撤销 moves the unpacked folder to the Recycle Bin and keeps the zip",
            undone is not null && !Directory.Exists(expected) && binned.Contains(Path.GetFileName(expected)) && File.Exists(zip),
            $"{how}; {undone ?? "no 'queue: undid' line within 10 s"}; folder exists={Directory.Exists(expected)}; Recycle Bin items from the folder: [{string.Join(", ", binned)}]");
    }
}
