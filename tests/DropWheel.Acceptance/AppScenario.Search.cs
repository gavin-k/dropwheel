using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    /// <summary>
    /// JPG → BMP, which a JPG's wheel does not offer until it is searched for: "bmx" matches nothing, then
    /// Backspace and "p" make "bmp", and a drop on its sector writes a bitmap next to the JPG.
    /// </summary>
    private static void SearchFormat(Checks checks, AppLog log, string dir, bool explorer)
    {
        var jpg = Path.Combine(dir, $"m1-search-{DateTime.Now:HHmmss}.jpg");
        WriteJpeg(jpg, 320, 200);
        var bmp = ConvertThroughWheel(checks, log, jpg, "bmp", explorer, search: "bmx\bp");

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

        checks.Check("keys typed on the wheel never reach the window under it", leaked.Count == 0 && button,
            $"{text.Length} keys, down while pressed: {(leaked.Count == 0 ? "none" : string.Join(", ", leaked))}; left button {(button ? "down throughout" : "seen up")}");
        return log.WaitFor(2000, $"wheel: search \"{query}\", slots");
    }

    private static bool Down(VIRTUAL_KEY vk) => (PInvoke.GetAsyncKeyState((int)vk) & 0x8000) != 0;
}
