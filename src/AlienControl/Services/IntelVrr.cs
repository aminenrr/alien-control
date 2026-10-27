using System;
using System.Runtime.InteropServices;

namespace AlienControl.Services;

/// <summary>
/// 2.0.3 (Amine): is VRR (Adaptive-Sync) on for the panel when the Intel GPU drives it (Optimus), for the Metal
/// level's VRR row. Read-only, through Intel's Graphics Control Library (ControlLib.dll in System32, the same library
/// IntelGpuTemp uses): ctlInit, ctlEnumerateDevices, ctlEnumerateDisplayOutputs, ctlGetDisplayProperties.
/// ctl_display_properties_t (igcl_api.h, github.com/intel/drivers.gpu.control-library), x64 natural packing:
///   uint32 Size @0; uint8 Version @4; union { uint32; void* } Os_display_encoder_handle @8; Type @16;
///   AttachedDisplayMuxType @20; ProtocolConverterOutput @24; ctl_revision_datatype_t (3 x uint8) @28;
///   SupportedOutputBPCFlags @32; ProtocolConverterType @36; DisplayConfigFlags @40; FeatureEnabledFlags @44;
///   FeatureSupportedFlags @48; AdvancedFeatureEnabledFlags @52; AdvancedFeatureSupportedFlags @56;
///   ctl_display_timing_t (64 bytes, holds a uint64) @64; uint32 ReservedFields[16] @128  =  192 bytes.
/// DisplayConfigFlags bit 0 = display active; FeatureEnabledFlags bit 3 = CTL_STD_DISPLAY_FEATURE_FLAG_ADAPTIVESYNC_VRR.
/// The answer is "VRR enabled on the active Intel output", like NVIDIA's bIsVRREnabled. Null when ControlLib is
/// missing, fails, or no Intel output is active (dGPU mode: the RTX drives the panel). A failed open disables it for
/// the session. Called from the overlay's extra poll (one call at a time, every 5 s with the refresh read).
/// </summary>
public sealed class IntelVrr : IDisposable
{
    internal const int PropsSize = 192, OffConfig = 40, OffEnabled = 44;
    internal const uint FlagActive = 1, FlagVrr = 1 << 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct CtlInitArgs
    {
        public uint Size; public byte Version; public uint AppVersion; public uint Flags; public uint SupportedVersion;
        public uint Uid1; public ushort Uid2, Uid3; public uint Uid4a, Uid4b;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn(ref CtlInitArgs args, out IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseFn(IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumFn(IntPtr parent, ref uint count, [Out] IntPtr[]? items);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PropsFn(IntPtr output, IntPtr props);

    private readonly object _lock = new();
    private IntPtr _lib, _api;
    private CloseFn? _close;
    private EnumFn? _enumDevices, _enumOutputs;
    private PropsFn? _props;
    private bool _tried, _disabled;

    /// <summary>VRR on / off for the active Intel display output, or null (no IGCL, no active Intel output).</summary>
    public bool? Read()
    {
        lock (_lock)
        {
            if (_disabled) return null;
            IntPtr buf = IntPtr.Zero;
            try
            {
                if (!_tried && !Open()) return null;
                uint n = 0;
                if (_enumDevices!(_api, ref n, null) != 0 || n == 0) return null;
                var devices = new IntPtr[n];
                if (_enumDevices(_api, ref n, devices) != 0) return null;
                buf = Marshal.AllocHGlobal(PropsSize);
                foreach (var dev in devices)
                {
                    if (dev == IntPtr.Zero) continue;
                    uint c = 0;
                    if (_enumOutputs!(dev, ref c, null) != 0 || c == 0) continue;
                    var outs = new IntPtr[c];
                    if (_enumOutputs(dev, ref c, outs) != 0) continue;
                    foreach (var o in outs)
                    {
                        if (o == IntPtr.Zero) continue;
                        for (int i = 0; i < PropsSize; i += 4) Marshal.WriteInt32(buf, i, 0);
                        Marshal.WriteInt32(buf, 0, PropsSize);   // Version 0
                        if (_props!(o, buf) != 0) continue;
                        var r = Parse((uint)Marshal.ReadInt32(buf, OffConfig), (uint)Marshal.ReadInt32(buf, OffEnabled));
                        if (r is bool on) return on;
                    }
                }
                return null;
            }
            catch (Exception ex) { Log.Warn("IntelVrr", ex); Disable(); return null; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }
    }

    /// <summary>An output's answer: null when it isn't active, else whether Adaptive-Sync / VRR is enabled.</summary>
    internal static bool? Parse(uint configFlags, uint enabledFlags) =>
        (configFlags & FlagActive) == 0 ? null : (enabledFlags & FlagVrr) != 0;

    private bool Open()
    {
        _tried = true;
        if (!NativeLibrary.TryLoad("ControlLib.dll", typeof(IntelVrr).Assembly, DllImportSearchPath.System32, out _lib))
        { _lib = IntPtr.Zero; _disabled = true; return false; }
        var init = Fn<InitFn>("ctlInit");
        _close = Fn<CloseFn>("ctlClose");
        _enumDevices = Fn<EnumFn>("ctlEnumerateDevices");
        _enumOutputs = Fn<EnumFn>("ctlEnumerateDisplayOutputs");
        _props = Fn<PropsFn>("ctlGetDisplayProperties");
        if (init is null || _enumDevices is null || _enumOutputs is null || _props is null) { Disable(); return false; }
        var args = new CtlInitArgs { Size = (uint)Marshal.SizeOf<CtlInitArgs>(), AppVersion = (1u << 16) | 1u };
        if (init(ref args, out _api) != 0 || _api == IntPtr.Zero) { _api = IntPtr.Zero; Disable(); return false; }
        return true;
    }

    private T? Fn<T>(string name) where T : Delegate =>
        NativeLibrary.TryGetExport(_lib, name, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;

    private void Disable() { _disabled = true; Close(); }

    private void Close()
    {
        try { if (_api != IntPtr.Zero) _close?.Invoke(_api); }
        catch (Exception ex) { Log.Warn("IntelVrr.Close", ex); }
        _api = IntPtr.Zero;
    }

    public void Dispose() { lock (_lock) Close(); }
}
