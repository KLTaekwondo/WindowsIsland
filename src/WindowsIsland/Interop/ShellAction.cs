using System.Runtime.InteropServices;

namespace WindowsIsland;

/// <summary>
/// 把交互交还 Windows：点通知就打开系统通知中心，由系统负责显示、激活、清理。
/// 不再自己猜 AUMID / 找窗口 / 冷启动——那套启发式对 QQ 这种经典 Win32 程序会拉起新进程。
/// </summary>
internal static class ShellAction
{
    private const byte VkLeftWindows = 0x5B;
    private const byte VkNone = 0x00;
    private const byte VkN = 0x4E;
    private const uint KeyUp = 0x0002;

    public static void OpenNotificationCenter()
    {
        try
        {
            keybd_event(VkLeftWindows, VkNone, 0, IntPtr.Zero);
            Thread.Sleep(50);
            keybd_event(VkN, VkNone, 0, IntPtr.Zero);
            keybd_event(VkN, VkNone, KeyUp, IntPtr.Zero);
            Thread.Sleep(50);
            keybd_event(VkLeftWindows, VkNone, KeyUp, IntPtr.Zero);
        }
        catch
        {
            // 快捷键发送失败就算了，不至于影响岛本身
        }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
}
