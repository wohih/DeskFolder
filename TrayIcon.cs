using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeskFolder;

/// <summary>
/// 轻量系统托盘图标（纯 Win32 实现，不依赖 WinForms / System.Drawing）。
/// 用于在不占用任务栏的情况下提供"全局主题设置 / 重新排列 / 开机自启动 / 退出"入口。
/// （单文件夹外观设置已移入各文件夹的右键菜单；双击不再打开设置，改为右键菜单选择。）
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_TRAY = 0x8001;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint WM_NULL = 0x0000;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_CHECKED = 0x00000008;
    private const int IDI_APPLICATION = 32512;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA pData);
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private NOTIFYICONDATA _data;
    private readonly Action _onSettings, _onRearrange, _onExit, _onToggleAutoStart, _onNewFolder;
    private readonly Func<bool> _isAutoStart;
    private bool _disposed;

    public TrayIcon(string tip, Action onSettings, Action onRearrange, Action onExit,
        Func<bool>? isAutoStart = null, Action? onToggleAutoStart = null, Action? onNewFolder = null)
    {
        _onSettings = onSettings;
        _onRearrange = onRearrange;
        _onExit = onExit;
        _isAutoStart = isAutoStart ?? (() => false);
        _onToggleAutoStart = onToggleAutoStart ?? (() => { });
        _onNewFolder = onNewFolder ?? (() => { });

        // 用一个隐藏的零尺寸窗口承载托盘回调消息
        var host = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = null,
            Visibility = Visibility.Hidden
        };
        host.Show();
        _hwnd = new WindowInteropHelper(host).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);

        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION),
            szTip = tip.Length > 127 ? tip.Substring(0, 127) : tip
        };
        Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY)
        {
            uint evt = (uint)lParam.ToInt32();
            if (evt == WM_RBUTTONUP)
            {
                ShowMenu();
                handled = true;
                return IntPtr.Zero;
            }
        }
        handled = false;
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, 1, "全局主题设置");
        AppendMenu(menu, MF_STRING, 2, "重新排列");
        AppendMenu(menu, MF_STRING, 5, "新建空白文件夹");
        uint autoFlags = MF_STRING | (_isAutoStart() ? MF_CHECKED : 0);
        AppendMenu(menu, autoFlags, 3, "开机自启动");
        AppendMenu(menu, MF_SEPARATOR, 0, string.Empty);
        AppendMenu(menu, MF_STRING, 4, "退出");
        GetCursorPos(out POINT pt);
        // 必须先把自己设为前台窗口，否则菜单拿不到焦点，点击别处不会消失（经典 TrackPopupMenu 坑）
        SetForegroundWindow(_hwnd);
        uint cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        // 让宿主窗口回到后台并消化一次消息，避免下次弹菜单被系统限制
        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        if (cmd == 1) _onSettings?.Invoke();
        else if (cmd == 2) _onRearrange?.Invoke();
        else if (cmd == 3) _onToggleAutoStart?.Invoke();
        else if (cmd == 4) _onExit?.Invoke();
        else if (cmd == 5) _onNewFolder?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shell_NotifyIcon(NIM_DELETE, ref _data);
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
    }
}
