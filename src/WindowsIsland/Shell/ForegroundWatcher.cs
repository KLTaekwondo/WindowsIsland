using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WindowsIsland;

/// <summary>
/// 监听前台窗口变化，判断两件事：焦点是否在「桌面」上、前台是否处于全屏。
/// 主路径是 SetWinEventHook(EVENT_SYSTEM_FOREGROUND)，事件驱动、零延迟；
/// 另有一个低频定时器兜底，防止个别场景下钩子漏事件。
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private static readonly Native.WinEventDelegate Callback = OnWinEvent;

    private static ForegroundWatcher? _current;

    private readonly DispatcherTimer _fallbackTimer;
    private IntPtr _hook;

    public ForegroundWatcher()
    {
        _fallbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _fallbackTimer.Tick += (_, _) => Evaluate();
    }

    /// <summary>桌面是否持有焦点。</summary>
    public bool IsDesktopFocused { get; private set; }

    /// <summary>前台窗口是否铺满了它所在显示器的整个画面（游戏 / 全屏视频）。</summary>
    public bool IsForegroundFullscreen { get; private set; }

    /// <summary>焦点状态翻转时触发，参数为「桌面是否聚焦」。</summary>
    public event EventHandler<bool>? DesktopFocusChanged;

    /// <summary>全屏状态翻转时触发。</summary>
    public event EventHandler<bool>? FullscreenChanged;

    public void Start()
    {
        _current = this;

        _hook = Native.SetWinEventHook(
            Native.EVENT_SYSTEM_FOREGROUND,
            Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            Callback,
            0,
            0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

        _fallbackTimer.Start();

        var foreground = Native.GetForegroundWindow();
        IsDesktopFocused = IsDesktop(foreground);
        IsForegroundFullscreen = IsFullscreen(foreground);
    }

    public void Dispose()
    {
        _fallbackTimer.Stop();

        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }

    private static void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (eventType != Native.EVENT_SYSTEM_FOREGROUND)
        {
            return;
        }

        _current?.Evaluate();
    }

    private void Evaluate()
    {
        var foreground = Native.GetForegroundWindow();

        var desktop = IsDesktop(foreground);
        if (desktop != IsDesktopFocused)
        {
            IsDesktopFocused = desktop;
            DesktopFocusChanged?.Invoke(this, desktop);
        }

        var fullscreen = IsFullscreen(foreground);
        if (fullscreen != IsForegroundFullscreen)
        {
            IsForegroundFullscreen = fullscreen;
            FullscreenChanged?.Invoke(this, fullscreen);
        }
    }

    private static bool IsDesktop(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var shell = Native.GetShellWindow();
        if (shell != IntPtr.Zero && (foreground == shell || Native.GetAncestor(foreground, Native.GA_ROOT) == shell))
        {
            return true;
        }

        var className = Native.GetClassNameSafe(foreground);
        if (Native.IsShellDesktopClass(className))
        {
            return true;
        }

        // 桌面图标位于 Progman / WorkerW 之下，根窗口类名同样可以佐证。
        var rootHwnd = Native.GetAncestor(foreground, Native.GA_ROOT);
        if (rootHwnd != IntPtr.Zero && rootHwnd != foreground)
        {
            return Native.IsShellDesktopClass(Native.GetClassNameSafe(rootHwnd));
        }

        return false;
    }

    private static bool IsFullscreen(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero || foreground == Native.GetShellWindow())
        {
            return false;
        }

        if (!Native.GetWindowRect(foreground, out var window))
        {
            return false;
        }

        var monitor = Native.MonitorFromWindow(foreground, Native.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var screen = info.rcMonitor;

        // 覆盖整块显示器才算全屏；最大化窗口会留下任务栏，因此不会误判。
        return window.Left <= screen.Left
            && window.Top <= screen.Top
            && window.Right >= screen.Right
            && window.Bottom >= screen.Bottom;
    }
}
