using System.Globalization;
using DropWheel.Core.Settings;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    /// <summary>How far a resting finger strays, in pixels, one entry per move: well inside the app's 10 DIP.</summary>
    private static readonly int[] Wobble = [0, 2, -1, 3, -2, 1, -3, 2];

    /// <summary>
    /// The touch and pen long press, with touch made of mouse moves signed the way Windows signs a finger's:
    /// mid-drag a mouse that rests opens nothing, a finger that rests opens the Convert wheel once the long
    /// press is up, and a JPG dropped through it on PNG, after the finger rests on that sector as well,
    /// comes out as a PNG without the wheel opening a second time.
    /// </summary>
    private static void TouchLongPress(Checks checks, AppLog log, string dir, bool explorer)
    {
        var jpg = Path.Combine(dir, $"m1-touch-{DateTime.Now:HHmmss}.jpg");
        WriteJpeg(jpg, 320, 200);
        RestOpensNothing(checks, log, jpg, explorer, 0, "a mouse resting mid-drag opens nothing");

        var png = ConvertThroughWheel(checks, log, jpg, "png", explorer, touch: true, beforeRelease: () => Rest(Cursor.Position, 700, Input.Touch));
        var shown = log.Count("wheel: shown");
        checks.Check("a finger resting on a sector of the open wheel does not open it again", shown == 1, $"{shown} 'wheel: shown' line(s)");
        checks.Check("PNG made through the long-press wheel lands next to the JPG", png is not null && SameDir(png, jpg) && png.EndsWith(".png", StringComparison.OrdinalIgnoreCase),
            png ?? "no output");
    }

    /// <summary>
    /// Rests a finger at <paramref name="mid"/> until the wheel opens and returns where the app put it, after
    /// checking it waited for the long press: from the first touch move to the app's "wheel: shown" line,
    /// both on this machine's clock.
    /// </summary>
    private static Wheel? Hold(Checks checks, AppLog log, Point mid)
    {
        var from = DateTime.Now;
        for (var i = 0; i < 40 && log.Find("wheel: shown") is null; i++)
        {
            Input.MoveTo(mid.X + Wobble[i % Wobble.Length], mid.Y + Wobble[(i + 3) % Wobble.Length], Input.Touch);
            Thread.Sleep(50);
        }

        var wheel = Shown(checks, log, mid, "long press", "a finger resting mid-drag", Input.Touch);
        var line = log.Find("wheel: shown");
        if (wheel is not null && line is { Length: > 23 }
            && DateTime.TryParseExact(line[..23], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            var hold = KeyBindings.Default.LongPressMs;
            var held = (at - from).TotalMilliseconds;
            checks.Check($"the wheel waits for the {hold} ms long press", held >= hold - 20 && held <= hold + 400,
                $"{held:F0} ms from the first touch move to 'wheel: shown'");
        }

        return wheel;
    }

    /// <summary>
    /// Drags <paramref name="source"/>, rests a second with moves signed <paramref name="extraInfo"/>, checks no
    /// wheel opened, then drops back on the source, which does nothing.
    /// </summary>
    private static void RestOpensNothing(Checks checks, AppLog log, string source, bool explorer, nuint extraInfo, string what)
    {
        using var drag = DragSource.Open(source, explorer);
        var mid = drag.PressAndDrag();
        log.Mark();
        Rest(mid, 1000, extraInfo);
        var opened = log.WaitFor(300, "wheel: shown");
        checks.Check(what, opened is null, opened ?? "no 'wheel: shown' within 1.3 s");
        Input.MoveSmooth(mid, drag.Grab, 10, 30);
        Thread.Sleep(250);
        Input.LeftUp();
        Thread.Sleep(300);
    }

    /// <summary>Moves within 3 px of <paramref name="at"/> every 50 ms for <paramref name="ms"/>, signed <paramref name="extraInfo"/>.</summary>
    private static void Rest(Point at, int ms, nuint extraInfo)
    {
        for (var i = 0; i * 50 < ms; i++)
        {
            Input.MoveTo(at.X + Wobble[i % Wobble.Length], at.Y + Wobble[(i + 3) % Wobble.Length], extraInfo);
            Thread.Sleep(50);
        }
    }
}
