using CommunityToolkit.Mvvm.Messaging;
using AgentStatus;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace AgentStatus;

internal sealed partial class TrayIconService
{
    private const uint MY_NOTIFY_ID = 1000;
    private const uint WM_TRAY_ICON = PInvoke.WM_USER + 1;

    private readonly uint WM_TASKBAR_RESTART;

    private Window? _window;
    private HWND _hwnd;
    private nint _originalWndProcPtr;
    private WNDPROC? _originalWndProc;
    private WNDPROC? _trayWndProc;
    private bool _wndProcRestored;
    private NOTIFYICONDATAW? _trayIconData;
    private DestroyIconSafeHandle? _largeIcon;
    private DestroyMenuSafeHandle? _popupMenu;

    public TrayIconService()
    {
        WM_TASKBAR_RESTART = PInvoke.RegisterWindowMessage("TaskbarCreated");
    }

    public void SetupTrayIcon(bool? showSystemTrayIcon = true)
    {
        if (showSystemTrayIcon ?? false)
        {
            if (_window is null)
            {
                _window = new Window();
                _hwnd = new HWND(WindowNative.GetWindowHandle(_window));

                // Prevent the helper window from appearing on the taskbar
                var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
                exStyle |= WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
                exStyle &= ~WINDOW_EX_STYLE.WS_EX_APPWINDOW;
                PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)exStyle);

                _trayWndProc = WindowProc;
                nint hotKeyPrcPointer = Marshal.GetFunctionPointerForDelegate(_trayWndProc);
                _originalWndProcPtr = PInvoke.SetWindowLongPtr(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_WNDPROC, hotKeyPrcPointer);
                if (_originalWndProcPtr != 0)
                {
                    _originalWndProc = Marshal.GetDelegateForFunctionPointer<WNDPROC>(_originalWndProcPtr);
                }
            }

            if (_trayIconData is null)
            {
                _largeIcon = GetAppIconHandle();
                _trayIconData = new NOTIFYICONDATAW()
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                    hWnd = _hwnd,
                    uID = MY_NOTIFY_ID,
                    uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
                    uCallbackMessage = WM_TRAY_ICON,
                    hIcon = (HICON)_largeIcon.DangerousGetHandle(),
                    szTip = "Agent Status - Taskbar Tray",
                };
            }

            NOTIFYICONDATAW d = (NOTIFYICONDATAW)_trayIconData;
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in d);

            if (_popupMenu is null)
            {
                string versionText = GetVersionString();
                _popupMenu = PInvoke.CreatePopupMenu_SafeHandle();
                PInvoke.InsertMenu(_popupMenu, 0, MENU_ITEM_FLAGS.MF_BYPOSITION | MENU_ITEM_FLAGS.MF_STRING | MENU_ITEM_FLAGS.MF_GRAYED, 0, versionText);
                PInvoke.InsertMenu(_popupMenu, 1, MENU_ITEM_FLAGS.MF_BYPOSITION | MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
                PInvoke.InsertMenu(_popupMenu, 2, MENU_ITEM_FLAGS.MF_BYPOSITION | MENU_ITEM_FLAGS.MF_STRING, PInvoke.WM_USER + 1, "Exit");
            }
        }
        else
        {
            Destroy();
        }
    }

    public void Destroy()
    {
        if (_trayIconData is not null)
        {
            NOTIFYICONDATAW d = (NOTIFYICONDATAW)_trayIconData;
            if (PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in d))
            {
                _trayIconData = null;
            }
        }

        if (_popupMenu is not null)
        {
            _popupMenu.Close();
            _popupMenu = null;
        }

        if (_largeIcon is not null)
        {
            _largeIcon.Close();
            _largeIcon = null;
        }

        if (_window is not null)
        {
            RestoreWindowProc();
            _window.Close();
            _window = null;
            _hwnd = HWND.Null;
        }
    }

    private void RestoreWindowProc()
    {
        if (_wndProcRestored || _originalWndProcPtr == 0 || _hwnd == HWND.Null)
        {
            return;
        }

        PInvoke.SetWindowLongPtr(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_WNDPROC, _originalWndProcPtr);
        _wndProcRestored = true;
    }

    private DestroyIconSafeHandle GetAppIconHandle()
    {
        string exePath = Path.Combine(AppContext.BaseDirectory, "AgentStatus.exe");
        PInvoke.ExtractIconEx(exePath, 0, out DestroyIconSafeHandle largeIcon, out _, 1);
        return largeIcon;
    }

    private static string GetVersionString()
    {
        try
        {
            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"Agent Status v{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return "Agent Status";
        }
    }

    private LRESULT WindowProc(
        HWND hwnd,
        uint uMsg,
        WPARAM wParam,
        LPARAM lParam)
    {
        switch (uMsg)
        {
            case PInvoke.WM_COMMAND:
                {
                    if (wParam == PInvoke.WM_USER + 1)
                    {
                        WeakReferenceMessenger.Default.Send<QuitMessage>();
                    }
                }

                break;

            case PInvoke.WM_WINDOWPOSCHANGING:
                {
                    if (_trayIconData is null)
                    {
                        SetupTrayIcon();
                    }
                }

                break;
            default:
                if (uMsg == WM_TASKBAR_RESTART)
                {
                    SetupTrayIcon();
                    WeakReferenceMessenger.Default.Send<TaskbarRestartMessage>(new());
                }
                else if (uMsg == WM_TRAY_ICON)
                {
                    switch ((uint)lParam.Value)
                    {
                        case PInvoke.WM_RBUTTONUP:
                            {
                                if (_popupMenu is not null)
                                {
                                    PInvoke.GetCursorPos(out System.Drawing.Point cursorPos);
                                    PInvoke.SetForegroundWindow(_hwnd);
                                    PInvoke.TrackPopupMenuEx(_popupMenu, (uint)TRACK_POPUP_MENU_FLAGS.TPM_LEFTALIGN | (uint)TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN, cursorPos.X, cursorPos.Y, _hwnd, null);
                                }
                            }

                            break;
                    }
                }

                break;
        }

        return _originalWndProc != null
            ? PInvoke.CallWindowProc(_originalWndProc, hwnd, uMsg, wParam, lParam)
            : (LRESULT)0;
    }
}
