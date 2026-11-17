using System.Diagnostics;
using Microsoft.Win32;

namespace WindowsIsland;

/// <summary>
/// Login auto-start via the per-user Run key (no admin rights needed).
/// We always write the *current* executable path, so moving the app and
/// re-toggling the option repairs the entry.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowsIsland";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns true when the registry now matches the requested state.</summary>
    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            if (key is null)
            {
                return false;
            }

            if (!enabled)
            {
                if (key.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }

                return true;
            }

            var exe = ExecutablePath();
            if (string.IsNullOrEmpty(exe))
            {
                return false;
            }

            key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Keeps the registry in sync with the saved setting (path may have changed).</summary>
    public static void Sync(bool enabled)
    {
        if (enabled)
        {
            Apply(true);
            return;
        }

        Apply(false);
    }

    private static string? ExecutablePath()
    {
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;

            if (!string.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            return Environment.ProcessPath;
        }
        catch
        {
            return null;
        }
    }
}
