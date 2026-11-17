using System.Diagnostics;
using System.IO;
using System.Text;

namespace WindowsIsland;

/// <summary>
/// 屏幕亮度：通过 PowerShell 的 CIM 调用读写（root/WMI 的 WmiMonitorBrightness）。
///
/// 为什么绕这么远：
/// 1. DDC/CI（dxva2）在这台机器上不可用（0xC0262582）；
/// 2. 直接 COM（WbemScripting）实测只能成功一次，之后一律 0x80004005；
/// 3. System.Management 是 NuGet 包，当前环境拉不到。
/// PowerShell 的 Get-CimInstance 反复调用稳定，代价是每次约 200~400ms，
/// 所以调用方必须放后台线程，且不要每帧调用。
/// </summary>
internal static class BrightnessController
{
    public static string? LastError { get; private set; }

    public static bool TryGetPercent(out int percent)
    {
        percent = 0;

        const string script = "(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness).CurrentBrightness";
        var output = RunPowerShell(script);

        if (int.TryParse(output.Trim(), out var value))
        {
            percent = Math.Clamp(value, 0, 100);
            LastError = null;
            return true;
        }

        LastError = string.IsNullOrWhiteSpace(output) ? "empty output" : output.Trim();
        return false;
    }

    public static bool TrySetPercent(int percent)
    {
        var target = Math.Clamp(percent, 0, 100);
        // 注意：CimInstance 上不能直接点方法，必须用 Invoke-CimMethod
        var script =
            "$m = Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods; " +
            $"Invoke-CimMethod -InputObject $m -MethodName WmiSetBrightness -Arguments @{{ Timeout = 1; Brightness = {target} }} | Out-Null; " +
            "(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness).CurrentBrightness";

        var output = RunPowerShell(script);

        if (int.TryParse(output.Trim(), out var applied) && Math.Abs(applied - target) <= 2)
        {
            LastError = null;
            return true;
        }

        LastError = string.IsNullOrWhiteSpace(output) ? "set: empty output" : output.Trim();
        return false;
    }

    /// <summary>跑一段 PowerShell 并取回 stdout。输出重定向到文件，避开管道限制。</summary>
    private static string RunPowerShell(string command)
    {
        // 每次调用用唯一文件名：多个实例（开发版 + 安装版）共用固定名字会互相覆盖/删除
        var unique = $"{Environment.ProcessId}_{Guid.NewGuid():N}";
        var temp = Path.Combine(Path.GetTempPath(), $"island-bright-{unique}.txt");

        try
        {
            File.Delete(temp);
        }
        catch
        {
            // 覆盖写即可
        }

        var scriptFile = Path.Combine(Path.GetTempPath(), $"island-bright-{unique}.ps1");

        try
        {
            File.WriteAllText(scriptFile, command, new UTF8Encoding(true));

            var script = $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptFile}\" > \"{temp}\" 2>&1";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + script,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit(5000);

            return File.Exists(temp) ? File.ReadAllText(temp, Encoding.UTF8).Trim() : string.Empty;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return string.Empty;
        }
        finally
        {
            try
            {
                File.Delete(temp);
                File.Delete(scriptFile);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
