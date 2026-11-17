using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsIsland;

/// <summary>
/// Reads the process memory number that Task Manager's "Memory" column shows:
/// the <b>private working set</b> (pages that are resident and not shared).
///
/// Managed APIs only expose WorkingSet64 (includes shared pages) and
/// PrivateMemorySize64 (private *commit*, includes paged-out pages), which is
/// why earlier the island showed ~66 MB while Task Manager showed ~41 MB.
/// PROCESS_MEMORY_COUNTERS_EX2 (Windows 10 1709+) carries the exact value.
/// </summary>
internal static class ProcessMemory
{
    /// <summary>Last Win32 error from the measurement (0 = ok, -1 = exception).</summary>
    public static int LastError { get; private set; }

    public static long PrivateWorkingSetBytes()
    {
        try
        {
            var info = new PmcEx2 { cb = (uint)Marshal.SizeOf<PmcEx2>() };

            if (!GetProcessMemoryInfo(GetCurrentProcess(), ref info, (uint)Marshal.SizeOf<PmcEx2>()))
            {
                LastError = Marshal.GetLastWin32Error();
                return 0;
            }

            var value = (long)info.PrivateWorkingSetSize;

            LastError = value > 0 ? 0 : Marshal.GetLastWin32Error();
            return value > 0 ? value : 0;
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PmcEx2
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public UIntPtr SharedCommitUsage;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref PmcEx2 counters, uint size);
}
