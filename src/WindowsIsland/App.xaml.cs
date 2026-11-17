using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WindowsIsland;

/// <summary>
/// Application entry point: single-instance guard plus manual main-window creation.
///
/// The mutex alone is not reliable: a crashed/killed instance can leave a process
/// that still holds the mutex but has no window (a "zombie"). In that case a new
/// launch used to exit silently and double-clicking the shortcut looked dead.
/// So we only defer to another instance when that instance actually owns a visible
/// window; otherwise we take over and start normally.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "WindowsIsland.SingleInstance.v2";
    private const string WindowTitle = "WindowsIsland";

    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Software rendering: this is a small floating overlay, D3D would only pull
        // tens of MB of GPU driver modules in for slightly smoother animation.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        _instanceMutex = new Mutex(true, MutexName, out var createdNew);

        if (!createdNew && HasLiveInstance())
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>True when another WindowsIsland process owns a visible window.</summary>
    private static bool HasLiveInstance()
    {
        var self = Environment.ProcessId;
        var found = false;

        try
        {
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);

                if (pid == self)
                {
                    return true;
                }

                var title = new StringBuilder(256);
                GetWindowText(hwnd, title, title.Capacity);

                if (!title.ToString().Contains(WindowTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Make sure the owning process is actually one of ours
                try
                {
                    var name = Process.GetProcessById((int)pid).ProcessName;
                    if (!string.Equals(name, "WindowsIsland", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    return true;
                }

                found = true;
                return false;
            }, IntPtr.Zero);
        }
        catch
        {
            // If detection fails, prefer starting over staying invisible.
            return false;
        }

        return found;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);


    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
}
