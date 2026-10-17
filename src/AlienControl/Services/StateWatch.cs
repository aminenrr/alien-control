using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AlienControl.Services;

/// <summary>
/// v0.9.13 idle trim: the status keys are event driven instead of polled every second.
/// Speaker / mic mute: one cached endpoint per direction with an IAudioEndpointVolumeCallback, re-bound when the default
/// device changes (IMMNotificationClient). Touchpad (F12): RegNotifyChangeKeyValue on its registry key.
/// Changed fires on a background thread whenever something may have changed; the reader compares and only then writes a frame.
/// Create and read on the UI thread.
/// </summary>
public sealed class StateWatch : IDisposable
{
    public event Action? Changed;

    private IMMDeviceEnumerator? _enum;
    private readonly IAudioEndpointVolume?[] _vol = new IAudioEndpointVolume?[2];
    private readonly VolumeCallback _volCb;
    private readonly DeviceCallback _devCb;
    private int _rebind;   // set by the device callback; next read re-binds the endpoints

    private RegistryKey? _tpKey;
    private AutoResetEvent? _tpEvent;
    private RegisteredWaitHandle? _tpWait;
    private const string TpPath = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status";

    public StateWatch()
    {
        _volCb = new VolumeCallback(this);
        _devCb = new DeviceCallback(this);
        try
        {
            _enum = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            _enum.RegisterEndpointNotificationCallback(_devCb);
            Bind();
        }
        catch { _enum = null; }
        WatchTouchpad();
    }

    internal void Raise() { try { Changed?.Invoke(); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("StateWatch.cs:45", __e); } }
    internal void DeviceChanged() { Interlocked.Exchange(ref _rebind, 1); Raise(); }

    public bool? SpeakerMuted() => Muted(0);
    public bool? MicMuted() => Muted(1);

    private bool? Muted(int flow)
    {
        if (Interlocked.Exchange(ref _rebind, 0) == 1) Bind();
        var v = _vol[flow];
        if (v is null) return null;
        try { return v.GetMute(out var m) == 0 ? m : null; }
        catch { return null; }
    }

    private void Bind()
    {
        if (_enum is null) return;
        for (int flow = 0; flow < 2; flow++)
        {
            Release(flow);
            try
            {
                if (_enum.GetDefaultAudioEndpoint(flow, 0, out var dev) != 0 || dev is null) continue;
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (dev.Activate(ref iid, 0x17, IntPtr.Zero, out var o) == 0 && o is IAudioEndpointVolume vol)
                {
                    vol.RegisterControlChangeNotify(_volCb);
                    _vol[flow] = vol;
                }
                Marshal.ReleaseComObject(dev);
            }
            catch (System.Exception __e) { AlienControl.Services.Log.Warn("StateWatch.cs:77", __e); }
        }
    }

    private void Release(int flow)
    {
        var v = _vol[flow];
        _vol[flow] = null;
        if (v is null) return;
        try { v.UnregisterControlChangeNotify(_volCb); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("StateWatch.cs:86", __e); }
        try { Marshal.ReleaseComObject(v); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("StateWatch.cs:87", __e); }
    }

    // ---- touchpad registry key ----
    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool subtree, int filter, SafeWaitHandle ev, bool async);
    private const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    private void WatchTouchpad()
    {
        try
        {
            _tpKey = Registry.CurrentUser.OpenSubKey(TpPath);
            if (_tpKey is null) return;
            _tpEvent = new AutoResetEvent(false);
            if (!Arm()) return;
            _tpWait = ThreadPool.RegisterWaitForSingleObject(_tpEvent, (_, _) => { Arm(); Raise(); }, null, Timeout.Infinite, false);
        }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("StateWatch.cs:105", __e); }
    }

    private bool Arm()
    {
        try { return _tpKey is not null && _tpEvent is not null && RegNotifyChangeKeyValue(_tpKey.Handle, false, REG_NOTIFY_CHANGE_LAST_SET, _tpEvent.SafeWaitHandle, true) == 0; }
        catch { return false; }
    }

    public void Dispose()
    {
        Release(0); Release(1);
        try { _enum?.UnregisterEndpointNotificationCallback(_devCb); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("StateWatch.cs:117", __e); }
        if (_enum is not null) try { Marshal.ReleaseComObject(_enum); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("StateWatch.cs:118", __e); }
        _enum = null;
        _tpWait?.Unregister(null); _tpWait = null;
        _tpKey?.Dispose(); _tpKey = null;
        _tpEvent?.Dispose(); _tpEvent = null;
    }

    // ---- COM ----
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly StateWatch _w;
        public VolumeCallback(StateWatch w) { _w = w; }
        public int OnNotify(IntPtr data) { _w.Raise(); return 0; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DeviceCallback : IMMNotificationClient
    {
        private readonly StateWatch _w;
        public DeviceCallback(StateWatch w) { _w = w; }
        public int OnDeviceStateChanged(string id, int state) { _w.DeviceChanged(); return 0; }
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) { _w.DeviceChanged(); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string id) { if (role == 0) _w.DeviceChanged(); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }

    [StructLayout(LayoutKind.Sequential)] public struct PropertyKey { public Guid fmtid; public int pid; }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
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
