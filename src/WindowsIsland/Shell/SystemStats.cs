using System.Runtime.InteropServices;

namespace WindowsIsland;

/// <summary>一次系统状态采样。</summary>
public readonly record struct SystemSnapshot(
    double CpuPercent,
    double MemoryPercent,
    int? BatteryPercent,
    bool Charging,
    bool OnAcPower);

/// <summary>
/// 用 Win32 直接取 CPU / 内存 / 电量，不依赖 PerformanceCounter 包。
/// CPU 用两次 GetSystemTimes 的增量算，第一次调用只建立基线。
/// </summary>
public sealed class SystemStatsSampler
{
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private bool _hasBaseline;

    public SystemSnapshot Sample()
    {
        var cpu = SampleCpu();
        var memory = SampleMemory();

        int? battery = null;
        var charging = false;
        var onAc = false;

        if (Native.GetSystemPowerStatus(out var power))
        {
            battery = power.BatteryLifePercent == 255 ? null : power.BatteryLifePercent;
            onAc = power.ACLineStatus == 1;
            charging = !onAc && ((Native.BatteryFlags)power.BatteryFlag).HasFlag(Native.BatteryFlags.Charging);
        }

        return new SystemSnapshot(cpu, memory, battery, charging, onAc);
    }

    private double SampleCpu()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0d;
        }

        var idleValue = idle.ToUInt64();
        var kernelValue = kernel.ToUInt64();
        var userValue = user.ToUInt64();

        if (!_hasBaseline)
        {
            _previousIdle = idleValue;
            _previousKernel = kernelValue;
            _previousUser = userValue;
            _hasBaseline = true;
            return 0d;
        }

        var idleDelta = idleValue - _previousIdle;
        var totalDelta = (kernelValue - _previousKernel) + (userValue - _previousUser);

        _previousIdle = idleValue;
        _previousKernel = kernelValue;
        _previousUser = userValue;

        if (totalDelta == 0)
        {
            return 0d;
        }

        return Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0d, 100d);
    }

    private static double SampleMemory()
    {
        var status = new Native.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>()
        };

        return Native.GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : 0d;
    }
}
