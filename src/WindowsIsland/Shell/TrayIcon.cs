using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WindowsIsland;

/// <summary>
/// 托盘图标与菜单，直接走 Shell_NotifyIcon + 原生弹出菜单。
/// 之前用 WinForms 的 NotifyIcon，会额外加载 System.Windows.Forms / System.Drawing（约 27MB 工作集）；
/// 换成纯 Win32 之后这部分完全省掉。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1

    private const int MenuIdVisible = 1;
    private const int MenuIdHover = 2;
    private const int MenuIdGlass = 6;
    private const int MenuIdAutoStart = 7;
    private const int MenuIdAlwaysOnTop = 3;
    private const int MenuIdRestart = 4;
    private const int MenuIdExit = 5;

    private readonly HwndSource _window;
    private readonly HwndSourceHook _hook;
    private readonly IntPtr _icon;
    private NOTIFYICONDATA _data;
    private bool _visible = true;
    private bool _hover = true;
    private bool _glass;
    private bool _autoStart;
    private bool _alwaysOnTop = true;

    public TrayIcon()
    {
        var parameters = new HwndSourceParameters("WindowsIslandTray")
        {
            Width = 1,
            Height = 1,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP，不显示、不进任务栏
            ExtendedWindowStyle = 0x00000080          // WS_EX_TOOLWINDOW
        };

        _window = new HwndSource(parameters);
        _hook = WndProc;
        _window.AddHook(_hook);

        _icon = LoadIconFromFile();
        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = "WindowsIsland"
        };

        Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    public Action? ExitRequested { get; set; }

    public Action? RestartRequested { get; set; }

    public Action<bool>? HoverToggled { get; set; }

    public Action<bool>? GlassToggled { get; set; }

    public Action<bool>? AutoStartToggled { get; set; }

    public Action<bool>? AlwaysOnTopToggled { get; set; }

    public Action<bool>? VisibleToggled { get; set; }

    public void SetVisible(bool visible)
    {
        if (_visible == visible)
        {
            return;
        }

        _visible = visible;
        VisibleToggled?.Invoke(visible);
    }

    /// <summary>只同步菜单勾选状态，不触发回调（用于启动时对齐设置）。</summary>
    public void SetHoverChecked(bool value) => _hover = value;

    public void SetAlwaysOnTopChecked(bool value) => _alwaysOnTop = value;

    public void SetGlassChecked(bool value) => _glass = value;

    public void SetAutoStartChecked(bool value) => _autoStart = value;

    public void Dispose()
    {
        Shell_NotifyIcon(NIM_DELETE, ref _data);

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }

        _window.RemoveHook(_hook);
        _window.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg != CallbackMessage)
        {
            return IntPtr.Zero;
        }

        switch ((int)lParam & 0xFFFF)
        {
            case WM_LBUTTONUP:
                _visible = !_visible;
                VisibleToggled?.Invoke(_visible);
                handled = true;
                break;

            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowMenu(hwnd);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING | (_visible ? MF_CHECKED : MF_UNCHECKED), MenuIdVisible, "显示灵动岛");
            AppendMenu(menu, MF_STRING | (_hover ? MF_CHECKED : MF_UNCHECKED), MenuIdHover, "悬停展开");
            AppendMenu(menu, MF_STRING | (_alwaysOnTop ? MF_CHECKED : MF_UNCHECKED), MenuIdAlwaysOnTop, "始终置顶");
            AppendMenu(menu, MF_STRING | (_glass ? MF_CHECKED : MF_UNCHECKED), MenuIdGlass, "液态玻璃外观");
            AppendMenu(menu, MF_STRING | (_autoStart ? MF_CHECKED : MF_UNCHECKED), MenuIdAutoStart, "开机自启动");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, MenuIdRestart, "重启");
            AppendMenu(menu, MF_STRING, MenuIdExit, "退出");

            GetCursorPos(out var cursor);

            // 托盘菜单的标准姿势：先把自己设为前台，否则点击别处菜单不会消失。
            SetForegroundWindow(hwnd);
            var command = TrackPopupMenu(
                menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
                cursor.X,
                cursor.Y,
                0,
                hwnd,
                IntPtr.Zero);
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case MenuIdVisible:
                    _visible = !_visible;
                    VisibleToggled?.Invoke(_visible);
                    break;

                case MenuIdAutoStart:
                    _autoStart = !_autoStart;
                    AutoStartToggled?.Invoke(_autoStart);
                    break;

                case MenuIdGlass:
                    _glass = !_glass;
                    GlassToggled?.Invoke(_glass);
                    break;

                case MenuIdHover:
                    _hover = !_hover;
                    HoverToggled?.Invoke(_hover);
                    break;

                case MenuIdAlwaysOnTop:
                    _alwaysOnTop = !_alwaysOnTop;
                    AlwaysOnTopToggled?.Invoke(_alwaysOnTop);
                    break;

                case MenuIdRestart:
                    RestartRequested?.Invoke();
                    break;

                case MenuIdExit:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// 优先从 exe 自身提取图标：单文件发布时 app.ico 不再作为独立文件存在，
    /// 但图标已经通过 ApplicationIcon 编译进 exe，提取出来最稳。
    /// </summary>
    private static IntPtr LoadIconFromFile()
    {
        try
        {
            var exe = Environment.ProcessPath;

            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var small = ExtractIconEx(exe, 0, out _, out var smallHandle, 1);
                if (small > 0 && smallHandle != IntPtr.Zero)
                {
                    return smallHandle;
                }
            }
        }
        catch
        {
            // fall through
        }

        var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(path))
        {
            var handle = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }
        }

        return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    // ---------------- interop ----------------

    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_SHOWTIP = 0x80;

    private const int WM_NULL = 0x0000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_UNCHECKED = 0x00000000;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const IntPtr IDI_APPLICATION = 32512;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Native.POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
