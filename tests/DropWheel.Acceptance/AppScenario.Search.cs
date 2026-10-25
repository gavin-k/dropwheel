using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    private const string WheelTitle = "DropWheel 轮盘";
    private const string HeicReason = "还不能写出 HEIC，体积差不多的可以选 AVIF";

    /// <summary>
    /// JPG → BMP, which a JPG's wheel does not offer until it is searched for: "bmx" matches nothing, then
    /// Backspace and "p" make "bmp", and a drop on its sector writes a bitmap next to the JPG. With the
    /// pointer on the sector the hub tells the expected quality.
    /// </summary>
    private static void SearchFormat(Checks checks, AppLog log, string dir, bool explorer)
    {
        var jpg = Path.Combine(dir, $"m1-search-{DateTime.Now:HHmmss}.jpg");
        WriteJpeg(jpg, 320, 200);
        string? hub = null;
        var bmp = ConvertThroughWheel(checks, log, jpg, "bmp", explorer, search: "bmx\bp", beforeRelease: () => hub = Hub());
        checks.Check("the hub tells the expected quality while the pointer is on BMP", hub == "预期质量高 | 无损保存，画质不会再降低", hub ?? "not read");

        var entered = log.Find("file(s) entered");
        checks.Check("a JPG's wheel does not offer BMP before typing", entered is not null && SlotOf(entered, "bmp") < 0,
            entered ?? "no 'file(s) entered' line");
        var none = log.Find("wheel: search \"bmx\", slots none");
        checks.Check("a search that matches no format empties the wheel", none is not null,
            none ?? log.Find("wheel: search \"bmx\"") ?? "no search line for bmx");
        if (bmp is null)
        {
            return;
        }

        var bytes = File.ReadAllBytes(bmp);
        checks.Check("BMP lands next to the JPG", SameDir(bmp, jpg) && bmp.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) && bytes is [(byte)'B', (byte)'M', ..],
            $"{bmp}: {bytes.Length} bytes");
    }

    /// <summary>
    /// HEIC, which the app reads but cannot write: searched for, it comes up off; with the pointer on it the hub says why, a screen reader hears
    /// the same, and a release there converts nothing.
    /// </summary>
    private static void UnavailableFormat(Checks checks, AppLog log, string jpg, bool explorer)
    {
        var before = Directory.GetFiles(Path.GetDirectoryName(jpg)!);
        using var source = DragSource.Open(jpg, explorer);
        var mid = source.PressAndDrag();
        log.Mark();
        if (Tap(checks, log, mid) is not { } w)
        {
            return;
        }

        var searched = log.WaitFor(2000, "file(s) entered") is null ? null : TypeSearch(checks, log, "heic");
        var sector = SlotOf(searched, "heic(off)");
        checks.Check("searching for HEIC finds it switched off", sector >= 0, searched ?? "no search line");
        if (sector < 0)
        {
            Input.MoveSmooth(mid, new Point(w.X, w.Y), 4, 30);
            Input.LeftUp();
            return;
        }

        Input.MoveSmooth(new Point(mid.X + 6, mid.Y + 6), SectorPoint(w, sector), 8, 30);
        Thread.Sleep(250);
        var hub = Hub();
        var name = Element(WheelTitle, $"Sector{sector}", out var how, byId: true)?.CurrentName.ToString() ?? how;
        Input.LeftUp();
        var closed = log.WaitFor(2000, "wheel: closed");
        Thread.Sleep(1000);
        var after = Directory.GetFiles(Path.GetDirectoryName(jpg)!);
        checks.Check("the hub says why HEIC is off while the pointer is on it", hub == $"HEIC 不可用 | {HeicReason}", hub);
        checks.Check("a screen reader hears the same reason", name == $"HEIC，匹配，不可用：{HeicReason}", name);
        checks.Check("release on the off sector converts nothing",
            closed is not null && !closed.Contains("dropped", StringComparison.Ordinal) && log.Find("wheel: enqueued") is null && after.Length == before.Length,
            $"{closed ?? "wheel never closed"}; files {before.Length} -> {after.Length}");
    }

    /// <summary>What the hub says, "title | subtitle", as UI Automation reads it.</summary>
    private static string Hub() =>
        $"{Element(WheelTitle, "WheelTitle", out _, byId: true)?.CurrentName.ToString()} | {Element(WheelTitle, "WheelSubtitle", out _, byId: true)?.CurrentName.ToString()}";

    /// <summary>
    /// Types <paramref name="text"/> on the open wheel, '\b' as Backspace, and returns the app's log line for
    /// the search it ends on. The hook must swallow every key, and a swallowed key never reaches the async
    /// key state, while the left button held for the drag does.
    /// </summary>
    private static string? TypeSearch(Checks checks, AppLog log, string text)
    {
        var query = "";
        var leaked = new List<string>();
        var button = true;
        foreach (var c in text)
        {
            var vk = c == '\b' ? VIRTUAL_KEY.VK_BACK : (VIRTUAL_KEY)char.ToUpperInvariant(c);
            Input.Key(vk, up: false);
            Thread.Sleep(20);
            if (Down(vk))
            {
                leaked.Add(c == '\b' ? "Backspace" : c.ToString());
            }

            button &= Down(VIRTUAL_KEY.VK_LBUTTON);
            Input.Key(vk, up: true);
            Thread.Sleep(40);
            query = c == '\b' ? query[..Math.Max(0, query.Length - 1)] : query + c;
        }

        checks.Check($"keys typed on the wheel never reach the window under it ({text.Replace("\b", "⌫", StringComparison.Ordinal)})", leaked.Count == 0 && button,
            $"{text.Length} keys, down while pressed: {(leaked.Count == 0 ? "none" : string.Join(", ", leaked))}; left button {(button ? "down throughout" : "seen up")}");
        return log.WaitFor(2000, $"wheel: search \"{query}\", slots");
    }

    private static bool Down(VIRTUAL_KEY vk) => (PInvoke.GetAsyncKeyState((int)vk) & 0x8000) != 0;
}
