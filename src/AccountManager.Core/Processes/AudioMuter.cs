using System.Runtime.InteropServices;

namespace AccountManager.Core.Processes;

/// <summary>Mutes or unmutes the audio sessions of specific processes using the Windows Core Audio (WASAPI) API.</summary>
public static class AudioMuter
{
    /// <summary>
    /// Sets the mute state of every audio session owned by one of <paramref name="processIds"/> on all active
    /// playback devices. Returns the number of sessions changed. Clients create their session a few seconds after
    /// starting, so callers should repeat this periodically.
    /// </summary>
    public static int SetMute(IReadOnlySet<int> processIds, bool mute)
    {
        if (processIds.Count == 0)
        {
            return 0;
        }

        int changed = 0;
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateActive, out devices) != 0 || devices is null)
            {
                return 0;
            }

            devices.GetCount(out uint deviceCount);
            for (uint d = 0; d < deviceCount; d++)
            {
                changed += SetMuteOnDevice(devices, d, processIds, mute);
            }
        }
        catch (COMException)
        {
            // Audio service unavailable or device removed mid-enumeration; try again next pass.
        }
        finally
        {
            Release(devices);
            Release(enumerator);
        }

        return changed;
    }

    private static int SetMuteOnDevice(IMMDeviceCollection devices, uint index, IReadOnlySet<int> processIds, bool mute)
    {
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        int changed = 0;
        try
        {
            if (devices.Item(index, out device) != 0 || device is null)
            {
                return 0;
            }

            Guid managerId = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref managerId, ClsCtxAll, 0, out object managerObject) != 0)
            {
                return 0;
            }

            manager = (IAudioSessionManager2)managerObject;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions is null)
            {
                return 0;
            }

            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl2? control = null;
                try
                {
                    if (sessions.GetSession(i, out control) != 0 || control is null)
                    {
                        continue;
                    }

                    if (control.GetProcessId(out uint pid) != 0 || !processIds.Contains((int)pid))
                    {
                        continue;
                    }

                    var volume = (ISimpleAudioVolume)control;
                    volume.GetMute(out bool current);
                    if (current != mute)
                    {
                        Guid context = Guid.Empty;
                        volume.SetMute(mute, ref context);
                        changed++;
                    }
                }
                finally
                {
                    Release(control);
                }
            }
        }
        finally
        {
            Release(sessions);
            Release(manager);
            Release(device);
        }

        return changed;
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }

    private const uint DeviceStateActive = 0x1;
    private const int ClsCtxAll = 0x17;

    private enum EDataFlow
    {
        Render = 0,
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
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(nint sessionGuid, uint flags, out nint control);

        [PreserveSig]
        int GetSimpleAudioVolume(nint sessionGuid, uint flags, out nint volume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetDisplayName(out nint name);

        [PreserveSig]
        int SetDisplayName(nint name, ref Guid context);

        [PreserveSig]
        int GetIconPath(out nint path);

        [PreserveSig]
        int SetIconPath(nint path, ref Guid context);

        [PreserveSig]
        int GetGroupingParam(out Guid grouping);

        [PreserveSig]
        int SetGroupingParam(ref Guid grouping, ref Guid context);

        [PreserveSig]
        int RegisterAudioSessionNotification(nint client);

        [PreserveSig]
        int UnregisterAudioSessionNotification(nint client);

        // IAudioSessionControl2
        [PreserveSig]
        int GetSessionIdentifier(out nint id);

        [PreserveSig]
        int GetSessionInstanceIdentifier(out nint id);

        [PreserveSig]
        int GetProcessId(out uint processId);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig]
        int SetMasterVolume(float level, ref Guid context);

        [PreserveSig]
        int GetMasterVolume(out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
