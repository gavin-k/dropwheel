using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropWheel.Shell;

/// <summary>The keyboard layouts the user has installed, as far as the wheel keys care.</summary>
public static unsafe class KeyboardLayouts
{
    /// <summary>
    /// True if any installed layout types a character with AltGr, which Windows reports as Ctrl+Alt
    /// (German, French, Polish…). The US layout and the Chinese IMEs on top of it have none.
    /// </summary>
    public static bool AnyHasAltGr()
    {
        var count = PInvoke.GetKeyboardLayoutList(0, null);
        if (count <= 0)
        {
            return false;
        }

        var layouts = new HKL[count];
        fixed (HKL* list = layouts)
        {
            count = PInvoke.GetKeyboardLayoutList(count, list);
        }

        return layouts.Take(count).Any(HasAltGr);
    }

    private static bool HasAltGr(HKL layout)
    {
        // VkKeyScanEx puts the shift state in the high byte: 2 is Ctrl, 4 is Alt. -1 means the layout has no such key.
        for (var c = '!'; c <= 'ɏ'; c++)
        {
            var scan = PInvoke.VkKeyScanEx(c, layout);
            if (scan != -1 && ((scan >> 8) & 6) == 6)
            {
                return true;
            }
        }

        return false;
    }
}
