using DropWheel.App.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DropWheel.App.Tray;

/// <summary>
/// Notification-area icon. Its messages arrive through a subclass of an app window that lives as long as
/// the process (the task window, which hides instead of closing), so it needs no window of its own. The
/// icon is added again when Explorer restarts and broadcasts TaskbarCreated. Left click or Enter raises
/// <see cref="Selected"/>; right click or Shift+F10 shows <paramref name="menu"/>. UI thread only.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint CallbackMessage = PInvoke.WM_APP + 0x10;
    private const uint IconId = 1;
    private const uint NinSelect = PInvoke.WM_USER;
    private const uint NinKeySelect = PInvoke.WM_USER | 1;
    private static readonly SUBCLASSPROC SubclassProc = OnMessage; // must outlive the subclass
    private static TrayIcon? _current; // one icon per process; the subclass callback finds it here
    private readonly HWND _hwnd;
    private readonly string _tip;
    private readonly IReadOnlyList<(string Label, Action Run)> _menu;
    private readonly uint _taskbarCreated;
    private HICON _icon;

    public TrayIcon(HWND hwnd, string tip, string iconPath, IReadOnlyList<(string Label, Action Run)> menu)
    {
        _hwnd = hwnd;
        _tip = tip;
        _menu = menu;
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        var size = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, PInvoke.GetDpiForWindow(hwnd));
        fixed (char* path = iconPath)
        {
            _icon = new HICON(PInvoke.LoadImage(default, path, GDI_IMAGE_TYPE.IMAGE_ICON, size, size, IMAGE_FLAGS.LR_LOADFROMFILE).Value);
        }

        _current = this;
        PInvoke.SetWindowSubclass(hwnd, SubclassProc, 2, 0);
        Add();
    }

    public event Action? Selected;

    public void Dispose()
    {
        var data = Data(0);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, &data);
        PInvoke.RemoveWindowSubclass(_hwnd, SubclassProc, 2);
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = default;
        }

        _current = null;
    }

    /// <summary>
    /// Shows a notification from the icon. Windows shows it as a toast, or not at all while notifications
    /// are off or Focus Assist is on; the call succeeds either way, so the log says sent, not shown.
    /// </summary>
    public void Notify(string title, string text)
    {
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_INFO);
        title.AsSpan().CopyTo(data.szInfoTitle.AsSpan());
        text.AsSpan().CopyTo(data.szInfo.AsSpan());
        data.dwInfoFlags = NOTIFY_ICON_INFOTIP_FLAGS.NIIF_WARNING;
        if (PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &data))
        {
            FileLog.Info($"tray: notification sent: {title}");
        }
        else
        {
            FileLog.Error($"tray: Shell_NotifyIcon(NIM_MODIFY) failed to send '{title}'");
        }
    }

    private void Add()
    {
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &data))
        {
            FileLog.Error("tray: Shell_NotifyIcon(NIM_ADD) failed");
            return;
        }

        data.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, &data);
        FileLog.Info("tray: icon added");
    }

    private NOTIFYICONDATAW Data(NOTIFY_ICON_DATA_FLAGS flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        _tip.AsSpan().CopyTo(data.szTip.AsSpan());
        return data;
    }

    private void ShowMenu(int x, int y)
    {
        var menu = PInvoke.CreatePopupMenu();
        for (var i = 0; i < _menu.Count; i++)
        {
            fixed (char* label = _menu[i].Label)
            {
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, (nuint)(i + 1), label);
            }
        }

        // Without the foreground switch the menu does not close when the user clicks elsewhere (KB135788).
        PInvoke.SetForegroundWindow(_hwnd);
        var command = PInvoke.TrackPopupMenuEx(menu, (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON), x, y, _hwnd, null);
        PInvoke.PostMessage(_hwnd, PInvoke.WM_NULL, default, default);
        PInvoke.DestroyMenu(menu);
        if (command.Value > 0 && command.Value <= _menu.Count)
        {
            _menu[command.Value - 1].Run();
        }
    }

    private static LRESULT OnMessage(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam, nuint id, nuint data)
    {
        if (_current is { } tray)
        {
            if (msg == CallbackMessage)
            {
                // NOTIFYICON_VERSION_4: the event is in LOWORD(lParam), the anchor point in wParam.
                switch ((uint)(lParam.Value & 0xFFFF))
                {
                    case NinSelect or NinKeySelect:
                        tray.Selected?.Invoke();
                        break;
                    case PInvoke.WM_CONTEXTMENU:
                        tray.ShowMenu((short)(wParam.Value & 0xFFFF), (short)((wParam.Value >> 16) & 0xFFFF));
                        break;
                }

                return new LRESULT(0);
            }

            if (msg == tray._taskbarCreated)
            {
                tray.Add();
            }
        }

        return PInvoke.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}
