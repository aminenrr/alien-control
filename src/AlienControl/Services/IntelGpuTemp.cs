using System;
using System.Runtime.InteropServices;

namespace AlienControl.Services;

/// <summary>
/// 2.0.0-beta1.4 (Amine): Intel GPU temperature for the Home / Tuning GPU tiles in iGPU only, read through Intel's
/// Graphics Control Library (IGCL, ControlLib.dll, installed in System32 by the Intel graphics driver). Read-only:
/// ctlInit, ctlEnumerateDevices, ctlEnumTemperatureSensors, ctlTemperatureGetProperties, ctlTemperatureGetState.
/// Struct layouts from Intel's public include/igcl_api.h (github.com/intel/drivers.gpu.control-library, API 1.1):
///   ctl_init_args_t       { uint32 Size; uint8 Version; uint32 AppVersion; uint32 flags; uint32 SupportedVersion;
///                           ctl_application_id_t ApplicationUID (16 bytes) }  = 36 bytes, natural packing
///   ctl_temp_properties_t { uint32 Size; uint8 Version; int32 type; double maxTemperature }  = 24 bytes
/// Handles are opaque pointers, every export is __cdecl (CTL_APICALL), results are ctl_result_t (0 = success).
/// The DLL is loaded from System32 only (LOAD_LIBRARY_SEARCH_SYSTEM32, as Intel's own cApiWrapper.cpp does).
/// Called from the sensor worker (Task.Run inside RefreshSensorsAsync, one call at a time). Any failure, a missing
/// DLL, or no GPU-type sensor disables IGCL for the session; the caller then falls back to the CPU package temperature.
/// </summary>
public sealed class IntelGpuTemp : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CtlInitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        // ctl_application_id_t { uint32 Data1; uint16 Data2, Data3; uint8 Data4[8] }, all zero = default. Data4 is two
        // uints, not a ulong: a ulong would 8-align it and make the struct 40 bytes instead of C's 36
        public uint Uid1; public ushort Uid2, Uid3; public uint Uid4a, Uid4b;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CtlTempProperties
    {
        public uint Size;
        public byte Version;
        public int Type;              // ctl_temp_sensors_t
        public double MaxTemperature;
    }

    /// <summary>Marshalled sizes, checked by the tests against igcl_api.h (36 and 24 bytes).</summary>
    internal static int InitArgsSize => Marshal.SizeOf<CtlInitArgs>();
    internal static int TempPropertiesSize => Marshal.SizeOf<CtlTempProperties>();

    private const int CTL_TEMP_SENSORS_GLOBAL = 0, CTL_TEMP_SENSORS_GPU = 1;
    private const uint CTL_INIT_FLAG_USE_LEVEL_ZERO = 1;   // telemetry / temperature need Level Zero
    private const uint CTL_APP_VERSION = (1u << 16) | 1u;  // CTL_MAKE_VERSION(1, 1)

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn(ref CtlInitArgs args, out IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseFn(IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumDevicesFn(IntPtr api, ref uint count, [Out] IntPtr[]? devices);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumTempFn(IntPtr device, ref uint count, [Out] IntPtr[]? sensors);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TempPropsFn(IntPtr sensor, ref CtlTempProperties props);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TempStateFn(IntPtr sensor, out double celsius);

    private readonly object _lock = new();
    private IntPtr _lib, _api, _sensor;
    private CloseFn? _close;
    private TempStateFn? _getState;
    private bool _tried;

    /// <summary>True once IGCL failed or has no usable sensor: the caller uses its fallback for the rest of the session.</summary>
    public bool Disabled { get; private set; }

    /// <summary>Why IGCL was disabled (for the one crash.log line), null while it works.</summary>
    public string? DisabledReason { get; private set; }

    /// <summary>Intel GPU temperature in °C, or null (IGCL missing, failed, or the value is out of range).</summary>
    public double? Read()
    {
        lock (_lock)
        {
            if (Disabled) return null;
            try
            {
                if (!_tried && !Open()) return null;
                if (_getState!(_sensor, out double t) != 0) { Disable("ctlTemperatureGetState failed"); return null; }
                if (double.IsNaN(t) || t <= 0 || t > 125) { Disable($"temperature out of range ({t:0.#})"); return null; }
                return t;
            }
            catch (Exception ex) { Log.Warn("IntelGpuTemp", ex); Disable(ex.GetType().Name); return null; }
        }
    }

    private bool Open()
    {
        _tried = true;
        if (!NativeLibrary.TryLoad("ControlLib.dll", typeof(IntelGpuTemp).Assembly, DllImportSearchPath.System32, out _lib))
        {
            _lib = IntPtr.Zero;
            Disable("ControlLib.dll not found");
            return false;
        }
        var init = Fn<InitFn>("ctlInit");
        _close = Fn<CloseFn>("ctlClose");
        var enumDevices = Fn<EnumDevicesFn>("ctlEnumerateDevices");
        var enumTemps = Fn<EnumTempFn>("ctlEnumTemperatureSensors");
        var props = Fn<TempPropsFn>("ctlTemperatureGetProperties");
        _getState = Fn<TempStateFn>("ctlTemperatureGetState");
        if (init is null || enumDevices is null || enumTemps is null || props is null || _getState is null)
        { Disable("ControlLib.dll lacks an export"); return false; }

        var args = new CtlInitArgs
        {
            Size = (uint)Marshal.SizeOf<CtlInitArgs>(), Version = 0,
            AppVersion = CTL_APP_VERSION, Flags = CTL_INIT_FLAG_USE_LEVEL_ZERO,
        };
        if (init(ref args, out _api) != 0 || _api == IntPtr.Zero) { _api = IntPtr.Zero; Disable("ctlInit failed"); return false; }

        uint n = 0;
        if (enumDevices(_api, ref n, null) != 0 || n == 0) { Disable("no Intel device"); return false; }
        var devices = new IntPtr[n];
        if (enumDevices(_api, ref n, devices) != 0) { Disable("ctlEnumerateDevices failed"); return false; }

        // the GPU-type sensor (max over the GPU's sensors); the device-wide max (GLOBAL) only when no GPU one exists
        IntPtr gpu = IntPtr.Zero, global = IntPtr.Zero;
        for (int d = 0; d < Math.Min(n, (uint)devices.Length); d++)
        {
            if (devices[d] == IntPtr.Zero) continue;
            uint c = 0;
            if (enumTemps(devices[d], ref c, null) != 0 || c == 0) continue;
            var sensors = new IntPtr[c];
            if (enumTemps(devices[d], ref c, sensors) != 0) continue;
            for (int i = 0; i < Math.Min(c, (uint)sensors.Length); i++)
            {
                if (sensors[i] == IntPtr.Zero) continue;
                var p = new CtlTempProperties { Size = (uint)Marshal.SizeOf<CtlTempProperties>(), Version = 0 };
                if (props(sensors[i], ref p) != 0) continue;
                if (p.Type == CTL_TEMP_SENSORS_GPU && gpu == IntPtr.Zero) gpu = sensors[i];
                else if (p.Type == CTL_TEMP_SENSORS_GLOBAL && global == IntPtr.Zero) global = sensors[i];
            }
            if (gpu != IntPtr.Zero) break;
        }
        _sensor = gpu != IntPtr.Zero ? gpu : global;
        if (_sensor == IntPtr.Zero) { Disable("no GPU temperature sensor"); return false; }
        return true;
    }

    private T? Fn<T>(string name) where T : Delegate =>
        NativeLibrary.TryGetExport(_lib, name, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;

    private void Disable(string why)
    {
        Disabled = true;
        DisabledReason = why;
        Close();
    }

    private void Close()
    {
        try { if (_api != IntPtr.Zero) _close?.Invoke(_api); }
        catch (Exception ex) { Log.Warn("IntelGpuTemp.Close", ex); }
        _api = _sensor = IntPtr.Zero;
        _getState = null;
        // ControlLib.dll stays loaded: freeing a driver library that may still hold Level Zero threads is not worth it
    }

    public void Dispose()
    {
        lock (_lock) Close();
    }
}
