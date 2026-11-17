using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WindowsIsland;

/// <summary>
/// 复刻「点击通知气泡」的动作，按可靠性从高到低尝试：
///
/// 1. **应用已经在跑 → 把它的窗口切到前台**（含托盘里的隐藏窗口）。这一步绝不会新建进程。
/// 2. 应用注册了 COM 激活器 → 调 <c>INotificationActivationCallback.Activate</c>，
///    这正是 Windows 点气泡时做的事（注册信息在
///    <c>HKCU/HKLM\Software\Classes\AppUserModelId\&lt;AUMID&gt;\CustomActivator</c>）。
/// 3. 都没命中（应用确实没开）→ 按 AUMID 启动，等同于系统的默认点击行为。
///
/// 注：通知 API 不暴露 toast 的 launch 参数，所以激活器只能收到空参数，
/// 对“打开主窗口”这类场景足够。
/// </summary>
internal static class ToastActivator
{
    private const string AppUserModelIdKey = @"Software\Classes\AppUserModelId";

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const int GW_OWNER = 4;

    public static bool TryClick(string? aumid, string appName)
    {
        var window = FindExistingWindow(aumid, appName);

        if (window != IntPtr.Zero)
        {
            FocusWindow(window);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(aumid))
        {
            var clsid = ReadActivatorClsid(aumid);
            if (clsid != Guid.Empty && InvokeActivator(clsid, aumid))
            {
                return true;
            }

            return ShellActivate(aumid);
        }

        return false;
    }

    /// <summary>找到该应用正在运行的进程可执行文件路径（用于取图标）。</summary>
    internal static string? ResolveExecutablePath(string? aumid, string appName)
    {
        var id = aumid?.Trim() ?? string.Empty;

        if (LooksLikePath(id) && File.Exists(id))
        {
            return id;
        }

        foreach (var candidate in BuildNameCandidates(id, appName))
        {
            var exact = FindProcessPath(candidate, exactOnly: true);
            if (exact is not null)
            {
                return exact;
            }
        }

        foreach (var candidate in BuildNameCandidates(id, appName))
        {
            var loose = FindProcessPath(candidate, exactOnly: false);
            if (loose is not null)
            {
                return loose;
            }
        }

        return null;
    }

    private static string? FindProcessPath(string normalized, bool exactOnly)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = Normalize(process.ProcessName);
                var match = exactOnly
                    ? string.Equals(name, normalized, StringComparison.Ordinal)
                    : name.Contains(normalized, StringComparison.Ordinal);

                if (!match)
                {
                    continue;
                }

                var path = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path))
                {
                    return path;
                }
            }
            catch
            {
                // 受保护进程读不到路径
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    // ---------------- 1) 找已存在的窗口 ----------------

    private static IntPtr FindExistingWindow(string? aumid, string appName)
    {
        var id = aumid?.Trim() ?? string.Empty;

        if (LooksLikePath(id) && File.Exists(id))
        {
            var byPath = FindByExecutablePath(id);
            if (byPath != IntPtr.Zero)
            {
                return byPath;
            }
        }

        if (id.Contains('!', StringComparison.Ordinal))
        {
            var byAumid = FindByWindowAumid(id);
            if (byAumid != IntPtr.Zero)
            {
                return byAumid;
            }
        }

        foreach (var candidate in BuildNameCandidates(id, appName))
        {
            var exact = FindByProcessName(candidate, exactOnly: true);
            if (exact != IntPtr.Zero)
            {
                return exact;
            }
        }

        foreach (var candidate in BuildNameCandidates(id, appName))
        {
            var loose = FindByProcessName(candidate, exactOnly: false);
            if (loose != IntPtr.Zero)
            {
                return loose;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> BuildNameCandidates(string aumid, string appName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var cleaned = Normalize(value);
            if (cleaned.Length >= 2)
            {
                set.Add(cleaned);
            }
        }

        var body = aumid.Contains('!', StringComparison.Ordinal) ? aumid[(aumid.LastIndexOf('!') + 1)..] : aumid;
        Add(body);

        var lastDot = body.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < body.Length - 1)
        {
            Add(body[(lastDot + 1)..]);
        }

        Add(appName);
        return set;
    }

    private static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        var result = sb.ToString();
        return result.EndsWith("exe", StringComparison.Ordinal) ? result[..^3] : result;
    }

    private static bool LooksLikePath(string value)
        => value.Contains(":\\", StringComparison.Ordinal) || value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static IntPtr FindByExecutablePath(string path)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainModule?.FileName is { } fileName
                    && string.Equals(fileName, path, StringComparison.OrdinalIgnoreCase))
                {
                    var window = MainWindowOf(process);
                    if (window != IntPtr.Zero)
                    {
                        return window;
                    }
                }
            }
            catch
            {
                // 受保护或已退出
            }
            finally
            {
                process.Dispose();
            }
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindByProcessName(string normalized, bool exactOnly)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = Normalize(process.ProcessName);
                var match = exactOnly
                    ? string.Equals(name, normalized, StringComparison.Ordinal)
                    : name.Contains(normalized, StringComparison.Ordinal);

                if (!match)
                {
                    continue;
                }

                var window = MainWindowOf(process);
                if (window != IntPtr.Zero)
                {
                    return window;
                }
            }
            catch
            {
                // 忽略
            }
            finally
            {
                process.Dispose();
            }
        }

        return IntPtr.Zero;
    }

    private static IntPtr MainWindowOf(Process process)
    {
        process.Refresh();

        if (process.MainWindowHandle is var handle && handle != IntPtr.Zero)
        {
            return handle;
        }

        // 托盘应用主窗口常常只是 SW_HIDE：优先可见窗口，退而求其次接受有标题的隐藏窗口
        var visible = IntPtr.Zero;
        var hidden = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != process.Id || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
            {
                return true;
            }

            var title = new StringBuilder(256);
            GetWindowText(hwnd, title, 256);
            if (title.Length == 0)
            {
                return true;
            }

            if (IsWindowVisible(hwnd))
            {
                visible = hwnd;
                return false;
            }

            if (hidden == IntPtr.Zero)
            {
                hidden = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return visible != IntPtr.Zero ? visible : hidden;
    }

    private static void FocusWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd))
        {
            ShowWindow(hwnd, SW_SHOW);
        }

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        var foreground = GetForegroundWindow();
        if (foreground == hwnd)
        {
            return;
        }

        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = false;

        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = AttachThreadInput(currentThread, foregroundThread, true);
            }

            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private static IntPtr FindByWindowAumid(string aumid)
    {
        var found = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
            {
                return true;
            }

            if (string.Equals(ReadWindowAumid(hwnd), aumid, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    // ---------------- 2) COM 激活器 ----------------

    private static Guid ReadActivatorClsid(string aumid)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey($@"{AppUserModelIdKey}\{aumid}");
                if (key?.GetValue("CustomActivator") is string raw && Guid.TryParse(raw.Trim('{', '}'), out var clsid))
                {
                    return clsid;
                }
            }
            catch
            {
                // 换下一个 hive
            }
        }

        return Guid.Empty;
    }

    private static bool InvokeActivator(Guid clsid, string aumid)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(clsid);
            if (type is null)
            {
                return false;
            }

            var instance = Activator.CreateInstance(type);

            try
            {
                if (instance is INotificationActivationCallback callback)
                {
                    callback.Activate(aumid, string.Empty, IntPtr.Zero, 0);
                    return true;
                }

                return false;
            }
            finally
            {
                if (instance is not null && Marshal.IsComObject(instance))
                {
                    Marshal.ReleaseComObject(instance);
                }
            }
        }
        catch
        {
            return false;
        }
    }

    // ---------------- 3) 兜底：按 AUMID 启动 ----------------

    private static bool ShellActivate(string aumid)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $@"shell:appsFolder\{aumid}",
                UseShellExecute = true
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- 窗口 AUMID ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, IntPtr pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, IntPtr pv);
        [PreserveSig] int Commit();
    }

    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    private static readonly PROPERTYKEY PKeyAppUserModelId = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5
    };

    private static string? ReadWindowAumid(IntPtr hwnd)
    {
        var iid = IID_IPropertyStore;

        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(32);

        try
        {
            for (var i = 0; i < 32; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            var key = PKeyAppUserModelId;
            if (store.GetValue(ref key, buffer) != 0)
            {
                return null;
            }

            var vt = (ushort)Marshal.ReadInt16(buffer);
            var pointer = Marshal.ReadIntPtr(buffer, 8);
            var value = vt == 31 && pointer != IntPtr.Zero ? Marshal.PtrToStringUni(pointer) : null;

            PropVariantClear(buffer);
            return value;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [ComImport]
    [Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INotificationActivationCallback
    {
        void Activate(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
            IntPtr data,
            uint count);
    }

    // ---------------- Win32 ----------------

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr pv);
}
