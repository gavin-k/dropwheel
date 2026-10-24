using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DragSpike;

/// <summary>SendInput helpers.  Coordinates are physical pixels on the virtual desktop (PerMonitorV2 process).</summary>
internal static class Input
{
    public static void MoveTo(int x, int y)
    {
        var vs = SystemInformation.VirtualScreen;
        var nx = (int)Math.Round((x - vs.X) * 65535.0 / (vs.Width - 1));
        var ny = (int)Math.Round((y - vs.Y) * 65535.0 / (vs.Height - 1));
        Send(new INPUT
        {
            type = INPUT_TYPE.INPUT_MOUSE,
            Anonymous =
            {
                mi = new MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK,
                },
            },
        });
    }

    public static void MoveSmooth(Point from, Point to, int steps, int stepDelayMs)
    {
        for (var i = 1; i <= steps; i++)
        {
            MoveTo(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps);
            Thread.Sleep(stepDelayMs);
        }
    }

    public static void LeftDown() => Mouse(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN);

    public static void LeftUp() => Mouse(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP);

    public static void Key(VIRTUAL_KEY vk, bool up)
    {
        Send(new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous =
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : default,
                },
            },
        });
    }

    private static void Mouse(MOUSE_EVENT_FLAGS flags)
    {
        Send(new INPUT { type = INPUT_TYPE.INPUT_MOUSE, Anonymous = { mi = new MOUSEINPUT { dwFlags = flags } } });
    }

    private static unsafe void Send(INPUT input)
    {
        ReadOnlySpan<INPUT> one = [input];
        var sent = PInvoke.SendInput(one, sizeof(INPUT));
        if (sent != 1)
        {
            Log.Info($"SendInput FAILED (err {Marshal.GetLastWin32Error()})");
        }
    }
}
