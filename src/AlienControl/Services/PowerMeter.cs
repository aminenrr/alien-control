using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AlienControl.Services;

/// <summary>
/// Power readout without third-party drivers (no HWiNFO, LibreHardwareMonitor or WinRing0):
///   CPU   = \EsifDeviceInformation(*)\RAPL Power (Intel DTT driver, value / 10 = W)
///   GPU   = NVML nvmlDeviceGetPowerUsage (nvml.dll ships with the NVIDIA driver)
///   Total = battery discharge rate (IOCTL_BATTERY_QUERY_STATUS), only on battery; on AC there's no wall meter.
/// Each part is null when its source isn't available. Values are 3-sample averages in whole watts.
/// Called from the sensor poll, so it only runs while the window is open.
/// </summary>
public sealed class PowerMeter : IDisposable
{
    public sealed record Reading(int? CpuW, int? GpuW, int? TotalW, bool OnBattery);

    private readonly Queue<double> _cpu = new(), _gpu = new(), _total = new();

    // ---------- CPU: Intel DTT RAPL counter ----------
    private PerformanceCounter? _rapl;
    private bool _raplTried;
    private int _raplZeroReads;

    private void PickRapl()
    {
        _raplTried = true;
        _rapl?.Dispose(); _rapl = null;
        try
        {
            if (!PerformanceCounterCategory.Exists("EsifDeviceInformation")) return;
            var cat = new PerformanceCounterCategory("EsifDeviceInformation");
            if (!cat.CounterExists("RAPL Power")) return;
            PerformanceCounter? best = null; double bestVal = 0; bool bestIsCpu = false;
            foreach (var inst in cat.GetInstanceNames())
            {
                var c = new PerformanceCounter("EsifDeviceInformation", "RAPL Power", inst, true);
                double v;
                try { v = c.NextValue(); } catch { c.Dispose(); continue; }
                // The processor participant (TCPU / B0D4) reports package power; others read 0.
                bool isCpu = inst.Contains("CPU", StringComparison.OrdinalIgnoreCase) || inst.Contains("B0D4", StringComparison.OrdinalIgnoreCase);
                if (v > 0 && (best is null || (isCpu && !bestIsCpu) || (isCpu == bestIsCpu && v > bestVal)))
                {
                    best?.Dispose();
                    best = c; bestVal = v; bestIsCpu = isCpu;
                }
                else c.Dispose();
            }
            _rapl = best;
        }
        catch { _rapl = null; }
    }

    private double? ReadCpu()
    {
        if (!_raplTried) PickRapl();
        if (_rapl is null) return null;
        try
        {
            double v = _rapl.NextValue() / 10.0;
            if (v <= 0 && ++_raplZeroReads > 15) { _raplZeroReads = 0; PickRapl(); }
            return v > 0 ? v : null;
        }
        catch { return null; }
    }

    // ---------- GPU: NVML ----------
    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    private bool _nvmlDead, _nvmlUp;
    private IntPtr _nvDevice;

    private double? ReadGpu()
    {
        if (_nvmlDead) return null;
        try
        {
            if (!_nvmlUp)
            {
                if (nvmlInit_v2() != 0) return null;              // driver present but GPU off (iGPU only): retry later
                if (nvmlDeviceGetHandleByIndex_v2(0, out _nvDevice) != 0) { nvmlShutdown(); return null; }
                _nvmlUp = true;
            }
            if (nvmlDeviceGetPowerUsage(_nvDevice, out uint mw) != 0) { nvmlShutdown(); _nvmlUp = false; return null; }
            return mw / 1000.0;
        }
        catch (DllNotFoundException) { _nvmlDead = true; }
        catch (EntryPointNotFoundException) { _nvmlDead = true; }
        catch { }
        return null;
    }

    // ---------- battery discharge rate ----------
    private static readonly Guid BatteryClass = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    private const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040, IOCTL_BATTERY_QUERY_STATUS = 0x29404C;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_WAIT_STATUS { public uint BatteryTag, Timeout, PowerState, LowCapacity, HighCapacity; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_STATUS { public uint PowerState, Capacity, Voltage; public int Rate; }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devs, IntPtr devInfo, ref Guid classGuid, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devs);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint inBuf, int inSize, out uint outBuf, int outSize, out int returned, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_WAIT_STATUS inBuf, int inSize, out BATTERY_STATUS outBuf, int outSize, out int returned, IntPtr ov);

    private string? _batteryPath;
    private bool _batteryTried;

    private static string? FindBatteryPath()
    {
        var guid = BatteryClass;
        IntPtr devs = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == IntPtr.Zero || devs == new IntPtr(-1)) return null;
        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            if (!SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref guid, 0, ref did)) return null;
            SetupDiGetDeviceInterfaceDetail(devs, ref did, IntPtr.Zero, 0, out int size, IntPtr.Zero);
            if (size <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize
                if (!SetupDiGetDeviceInterfaceDetail(devs, ref did, buf, size, out _, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(buf + 4);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }
    }

    /// <summary>Battery discharge in W (positive), or null when charging/unknown.</summary>
    private double? ReadDischarge()
    {
        if (!_batteryTried) { _batteryTried = true; try { _batteryPath = FindBatteryPath(); } catch { } }
        if (_batteryPath is null) return null;
        try
        {
            using var h = CreateFile(_batteryPath, 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h.IsInvalid) return null;
            uint wait = 0;
            if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, ref wait, 4, out uint tag, 4, out _, IntPtr.Zero) || tag == 0) return null;
            var ws = new BATTERY_WAIT_STATUS { BatteryTag = tag };
            if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_STATUS, ref ws, Marshal.SizeOf<BATTERY_WAIT_STATUS>(), out BATTERY_STATUS st, Marshal.SizeOf<BATTERY_STATUS>(), out _, IntPtr.Zero))
                return null;
            if (st.Rate == unchecked((int)0x80000000) || st.Rate >= 0) return null;
            return -st.Rate / 1000.0;
        }
        catch { return null; }
    }

    private static int? Avg(Queue<double> q, double? v)
    {
        if (v is null) { q.Clear(); return null; }
        q.Enqueue(v.Value);
        while (q.Count > 3) q.Dequeue();
        return (int)Math.Round(q.Average());
    }

    private static bool OnBatteryNow() =>
        System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;

    /// <summary>One sample (blocking; call off the UI thread).</summary>
    public Reading Sample()
    {
        bool battery = OnBatteryNow();
        int? cpu = Avg(_cpu, ReadCpu());
        // On battery NVML is skipped: querying it can wake a sleeping RTX and cost battery. Total covers it there.
        int? gpu = Avg(_gpu, battery ? null : ReadGpu());
        int? total = battery ? Avg(_total, ReadDischarge()) : Avg(_total, null);
        return new Reading(cpu, gpu, total, battery);
    }

    /// <summary>RAPL instances and their values, for the diagnostic report.</summary>
    public static string Describe()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("EsifDeviceInformation")) return "EsifDeviceInformation counters: not present (Intel DTT driver missing?)";
            var cat = new PerformanceCounterCategory("EsifDeviceInformation");
            if (!cat.CounterExists("RAPL Power")) return "EsifDeviceInformation: no 'RAPL Power' counter";
            return "RAPL Power: " + string.Join(", ", cat.GetInstanceNames().Select(i =>
            {
                try { using var c = new PerformanceCounter("EsifDeviceInformation", "RAPL Power", i, true); return $"{i}={c.NextValue()}"; }
                catch { return $"{i}=?"; }
            }));
        }
        catch (Exception ex) { return "RAPL: " + ex.Message; }
    }

    public void Dispose()
    {
        _rapl?.Dispose();
        if (_nvmlUp) { try { nvmlShutdown(); } catch { } _nvmlUp = false; }
    }
}
