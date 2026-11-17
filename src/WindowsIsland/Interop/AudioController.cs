using System.Runtime.InteropServices;

namespace WindowsIsland;

/// <summary>
/// System volume via CoreAudio.
///
/// Every call creates its own COM objects and releases them before returning.
/// COM interfaces are apartment-bound: our reads happen on a background thread
/// while drags happen on the UI thread, and caching the interface across threads
/// caused intermittent failures. Re-activating the endpoint is cheap.
/// </summary>
internal static class AudioController
{
    public static bool TryGet(out double volume, out bool muted)
    {
        volume = 0;
        muted = false;

        if (!TryActivate(out var endpoint))
        {
            return false;
        }

        try
        {
            if (endpoint.GetMasterVolumeLevelScalar(out var scalar) != 0)
            {
                return false;
            }

            if (endpoint.GetMute(out var isMuted) != 0)
            {
                return false;
            }

            volume = scalar;
            muted = isMuted;
            return true;
        }
        finally
        {
            Release(endpoint);
        }
    }

    public static bool TrySetVolume(double scalar)
    {
        if (!TryActivate(out var endpoint))
        {
            return false;
        }

        try
        {
            return endpoint.SetMasterVolumeLevelScalar((float)Math.Clamp(scalar, 0d, 1d), Guid.Empty) == 0;
        }
        finally
        {
            Release(endpoint);
        }
    }

    public static bool TrySetMute(bool mute)
    {
        if (!TryActivate(out var endpoint))
        {
            return false;
        }

        try
        {
            return endpoint.SetMute(mute, Guid.Empty) == 0;
        }
        finally
        {
            Release(endpoint);
        }
    }

    public static bool TryToggleMute(out bool nowMuted)
    {
        nowMuted = false;

        if (!TryGet(out _, out var muted))
        {
            return false;
        }

        var target = !muted;

        if (!TrySetMute(target))
        {
            return false;
        }

        nowMuted = target;
        return true;
    }

    private static bool TryActivate(out IAudioEndpointVolume endpoint)
    {
        endpoint = null!;
        IMMDevice? device = null;

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            try
            {
                if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device) != 0 || device is null)
                {
                    return false;
                }
            }
            finally
            {
                ReleaseObject(enumerator);
            }

            var iid = typeof(IAudioEndpointVolume).GUID;

            if (device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var instance) != 0
                || instance is not IAudioEndpointVolume volume)
            {
                return false;
            }

            endpoint = volume;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (device is not null)
            {
                ReleaseObject(device);
            }
        }
    }

    private static void Release(IAudioEndpointVolume endpoint) => ReleaseObject(endpoint);

    private static void ReleaseObject(object comObject)
    {
        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // already gone
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(int access, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out int count);

        [PreserveSig]
        int SetMasterVolumeLevel(float level, Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float level);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float level, Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float level);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
