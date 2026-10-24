using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AlienControl.Services;

/// <summary>
/// RAM, dedicated VRAM, CPU % and GPU % usage, read in-process (no CLI, no extra processes).
/// VRAM uses the Windows "GPU Adapter Memory" counters through one PDH query (2.0.0-beta1.4); the adapter with the
/// most dedicated memory in use is taken as the RTX 3070 Ti. One caller at a time (CPU % keeps a delta).
/// </summary>
public sealed class SystemStats : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    private readonly ulong _vramTotal = ReadVramTotal();

    /// <summary>(used GB, total GB)</summary>
    public static (double Used, double Total)? Ram()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return null;
        const double gb = 1024d * 1024 * 1024;
        return ((m.ullTotalPhys - m.ullAvailPhys) / gb, m.ullTotalPhys / gb);
    }

    // ---------- 2.0.0-beta1.4 (services review #5, ui review #5): one PDH query ----------
    // Was: PerformanceCounterCategory("GPU Engine").ReadCategory() every 2 s on the UI thread (every counter of every
    // engine instance, hundreds of KB of garbage per call) plus a new PerformanceCounter per "GPU Adapter Memory"
    // instance every 30 s. Now one PDH query holds two wildcard counters, opened once; each Home tick is one
    // PdhCollectQueryData and two PdhGetFormattedCounterArray calls into a reused buffer, on the sensor worker thread.
    // Wildcards pick up new instances by themselves, so the 30 s rescan is gone. Read-only Windows counters: no NVML,
    // the RTX can stay asleep.

    private const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2, PDH_CSTATUS_VALID_DATA = 0, PDH_CSTATUS_NEW_DATA = 1;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhRemoveCounter(IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);

    private const string Engine3DPath = @"\GPU Engine(*engtype_3D)\Utilization Percentage";
    private const string EngineAllPath = @"\GPU Engine(*)\Utilization Percentage";
    private const string VramPath = @"\GPU Adapter Memory(*)\Dedicated Usage";

    private readonly object _pdhLock = new();
    private IntPtr _query, _engines, _vramCounter;
    private bool _pdhTried, _engineAll;
    private int _emptyEngineReads;
    private long _lastCollect;              // Environment.TickCount64 of the last collect
    private IntPtr _buf;                    // reused PDH_FMT_COUNTERVALUE_ITEM_W buffer
    private uint _bufSize;
    private double? _gpuUse;                // 3D % from the last collect (null until two collects)
    private (double Used, double Total)? _vramUse;

    private bool OpenPdh()
    {
        if (_query != IntPtr.Zero) return true;
        if (_pdhTried) return false;
        _pdhTried = true;
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return false; }
            if (PdhAddEnglishCounterW(_query, VramPath, IntPtr.Zero, out _vramCounter) != 0) _vramCounter = IntPtr.Zero;
            AddEngineCounter(all: false);
            return true;
        }
        catch (Exception ex) { Log.Warn("SystemStats.Pdh", ex); ClosePdh(); return false; }
    }

    /// <summary>The engtype_3D wildcard keeps the array small. If it never returns an instance (a PDH that doesn't
    /// match part of an instance name), the plain (*) wildcard is used and filtered here.</summary>
    private void AddEngineCounter(bool all)
    {
        if (_engines != IntPtr.Zero) { PdhRemoveCounter(_engines); _engines = IntPtr.Zero; }
        _engineAll = all;
        if (PdhAddEnglishCounterW(_query, all ? EngineAllPath : Engine3DPath, IntPtr.Zero, out _engines) != 0)
        {
            _engines = IntPtr.Zero;
            if (!all) AddEngineCounter(all: true);
        }
    }

    private void ClosePdh()
    {
        if (_query != IntPtr.Zero) { try { PdhCloseQuery(_query); } catch (Exception ex) { Log.Warn("SystemStats.Close", ex); } }
        _query = _engines = _vramCounter = IntPtr.Zero;
        if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; _bufSize = 0; }
    }

    /// <summary>Runs fn(name, value) for every valid item of a wildcard counter. False when the read failed.</summary>
    private bool ForEachItem(IntPtr counter, Action<string, double> fn)
    {
        if (counter == IntPtr.Zero) return false;
        uint bytes = _bufSize;
        uint r = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bytes, out uint count, _buf);
        if (r == PDH_MORE_DATA || (_buf == IntPtr.Zero && bytes > 0))
        {
            if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
            _bufSize = Math.Max(bytes, _bufSize * 2) + 4096;   // headroom: instances come and go
            _buf = Marshal.AllocHGlobal((int)_bufSize);
            bytes = _bufSize;
            r = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bytes, out count, _buf);
        }
        if (r != 0) return false;
        // PDH_FMT_COUNTERVALUE_ITEM_W: LPWSTR szName; PDH_FMT_COUNTERVALUE { DWORD CStatus; (pad); double }
        int item = IntPtr.Size + 16;
        for (int i = 0; i < count; i++)
        {
            IntPtr at = _buf + i * item;
            uint status = (uint)Marshal.ReadInt32(at + IntPtr.Size);
            if (status is not (PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA)) continue;
            double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(at + IntPtr.Size + 8));
            fn(Marshal.PtrToStringUni(Marshal.ReadIntPtr(at)) ?? "", v);
        }
        return true;
    }

    /// <summary>One collect for both counters (at most every 500 ms, so Vram() then GpuUsage() share it).</summary>
    private void Collect()
    {
        long now = Environment.TickCount64;
        if (now - _lastCollect < 500 && _lastCollect != 0) return;
        _lastCollect = now;
        if (!OpenPdh() || PdhCollectQueryData(_query) != 0) { _vramUse = null; _gpuUse = null; return; }

        double best = -1; string? bestLuid = null;
        bool vramOk = ForEachItem(_vramCounter, (name, v) => { if (v > best) { best = v; bestLuid = LuidOf(name); } });
        const double gb = 1024d * 1024 * 1024;
        if (vramOk && best >= 0)
        {
            _rtxLuid = bestLuid ?? _rtxLuid;
            _vramUse = (best / gb, _vramTotal > 0 ? _vramTotal / gb : 0);
        }
        else _vramUse = null;

        if (_rtxLuid is not { } luid) { _gpuUse = null; return; }
        double sum = 0; int seen = 0, all = 0;
        bool engOk = ForEachItem(_engines, (name, v) =>
        {
            all++;
            if (_engineAll && !name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) return;
            if (!name.Contains(luid, StringComparison.OrdinalIgnoreCase)) return;
            sum += v; seen++;
        });
        if (engOk && !_engineAll && all == 0 && ++_emptyEngineReads >= 3) AddEngineCounter(all: true);
        else if (all > 0) _emptyEngineReads = 0;
        _gpuUse = engOk ? (seen > 0 ? Math.Clamp(sum, 0, 100) : 0) : null;
    }

    /// <summary>(used GB, total GB) of the discrete GPU's dedicated memory. Call off the UI thread.</summary>
    public (double Used, double Total)? Vram()
    {
        try { lock (_pdhLock) { Collect(); return _vramUse; } }
        catch (Exception ex) { Log.Warn("SystemStats.Vram", ex); return null; }
    }

    // ---------- 1.3.0-beta.1 CPU % and GPU % (Part 2), read only while the window is open ----------

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    private long _idle, _kernel, _user;

    /// <summary>CPU use since the last call, 0..100 (GetSystemTimes: no perf counter, no process). Null on the first call.</summary>
    public double? CpuUsage()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return null;
        long di = idle - _idle, dk = kernel - _kernel, du = user - _user;   // kernel time includes idle
        bool first = _kernel == 0;
        _idle = idle; _kernel = kernel; _user = user;
        long total = dk + du;
        if (first || total <= 0) return null;
        return Math.Clamp((total - di) * 100.0 / total, 0, 100);
    }

    private string? _rtxLuid;   // "luid_0x..._0x..." of the adapter with the most dedicated memory in use (the RTX)

    private static string? LuidOf(string instance)
    {
        var m = System.Text.RegularExpressions.Regex.Match(instance, @"luid_0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+");
        return m.Success ? m.Value : null;
    }

    /// <summary>RTX 3D engine use, 0..100, summed over its engtype_3D instances (no NVML, so it doesn't wake the RTX).
    /// Null until two samples exist or when the RTX isn't known yet. Call off the UI thread.</summary>
    public double? GpuUsage()
    {
        try { lock (_pdhLock) { Collect(); return _gpuUse; } }
        catch (Exception ex) { Log.Warn("GpuUsage", ex); return null; }
    }

    /// <summary>Largest HardwareInformation.qwMemorySize under the display adapter class key.</summary>
    private static ulong ReadVramTotal()
    {
        ulong best = 0;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return 0;
            foreach (var name in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                try
                {
                    using var k = cls.OpenSubKey(name);
                    var v = k?.GetValue("HardwareInformation.qwMemorySize");
                    ulong size = v switch { long l => (ulong)l, byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0), _ => 0 };
                    if (size > best) best = size;
                }
                catch (System.Exception __e) { AlienControl.Services.Log.Warn("SystemStats.cs:80", __e); }
            }
        }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("SystemStats.cs:83", __e); }
        return best;
    }

    public void Dispose()
    {
        lock (_pdhLock) ClosePdh();
    }
}
