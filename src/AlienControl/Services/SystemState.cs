using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AlienControl.Services;

/// <summary>
/// Cheap reads of the states the keyboard's status keys used to show
/// (speaker mute, mic mute, touchpad off). No CLI calls, safe to poll.
/// </summary>
public static class SystemState
{
    /// <summary>Default speaker muted. Null if unreadable.</summary>
    public static bool? SpeakerMuted() => EndpointMuted(0 /* eRender */);

    /// <summary>Default microphone muted. Null if unreadable or no mic.</summary>
    public static bool? MicMuted() => EndpointMuted(1 /* eCapture */);

    /// <summary>True when the precision touchpad is turned off (F12 T-PAD). Null if unknown.</summary>
    public static bool? TouchpadOff()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status");
            if (k?.GetValue("Enabled") is int v) return v == 0;
        }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("SystemState.cs:27", __e); /* ignore */ }
        return null;
    }

    private static bool? EndpointMuted(int dataFlow)
    {
        object? enumObj = null, devObj = null, volObj = null;
        try
        {
            enumObj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!);
            var enumerator = (IMMDeviceEnumerator)enumObj!;
            if (enumerator.GetDefaultAudioEndpoint(dataFlow, 0 /* eConsole */, out var dev) != 0 || dev is null) return null;
            devObj = dev;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var vol) != 0 || vol is null) return null;
            volObj = vol;
            if (((IAudioEndpointVolume)vol).GetMute(out var muted) != 0) return null;
            return muted;
        }
        catch { return null; }
        finally
        {
            foreach (var o in new[] { volObj, devObj, enumObj })
                if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
