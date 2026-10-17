using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AlienControl.Services;

/// <summary>
/// RAM and dedicated VRAM usage, read in-process (no CLI, no extra processes).
/// VRAM uses the Windows "GPU Adapter Memory" performance counters; the adapter with the
/// biggest dedicated memory is the RTX 3070 Ti.
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

    private PerformanceCounter[]? _vram;
    private DateTime _nextCounterScan = DateTime.MinValue;
    private readonly ulong _vramTotal = ReadVramTotal();

    /// <summary>(used GB, total GB)</summary>
    public static (double Used, double Total)? Ram()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return null;
        const double gb = 1024d * 1024 * 1024;
        return ((m.ullTotalPhys - m.ullAvailPhys) / gb, m.ullTotalPhys / gb);
    }

    /// <summary>(used GB, total GB) of the discrete GPU's dedicated memory.</summary>
    public (double Used, double Total)? Vram()
    {
        try
        {
            if (_vram is null || DateTime.Now >= _nextCounterScan)
            {
                // Instances change when the GPU sleeps/wakes; rescan every 30 s.
                foreach (var c in _vram ?? Array.Empty<PerformanceCounter>()) c.Dispose();
                var cat = new PerformanceCounterCategory("GPU Adapter Memory");
                _vram = cat.GetInstanceNames()
                    .Select(n => new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", n, true))
                    .ToArray();
                _nextCounterScan = DateTime.Now.AddSeconds(30);
            }
            if (_vram.Length == 0) return null;
            double used = 0;
            foreach (var c in _vram)
            {
                double v; try { v = c.RawValue; } catch { v = 0; }
                if (v > used) { used = v; _rtxLuid = LuidOf(c.InstanceName); }
            }
            const double gb = 1024d * 1024 * 1024;
            double total = _vramTotal > 0 ? _vramTotal / gb : 0;
            return (used / gb, total);
        }
        catch { return null; }
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
    private PerformanceCounterCategory? _engines;
    private System.Collections.Generic.Dictionary<string, CounterSample> _prevEng = new();

    private static string? LuidOf(string instance)
    {
        var m = System.Text.RegularExpressions.Regex.Match(instance, @"luid_0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+");
        return m.Success ? m.Value : null;
    }

    /// <summary>RTX 3D engine use, 0..100, from Windows' own GPU Engine counters (one ReadCategory per call, no NVML,
    /// so it doesn't wake the RTX). Null until two samples exist or when the RTX isn't known yet.</summary>
    public double? GpuUsage()
    {
        try
        {
            if (_rtxLuid is null) Vram();
            if (_rtxLuid is not { } luid) return null;
            _engines ??= new PerformanceCounterCategory("GPU Engine");
            var data = _engines.ReadCategory()["utilization percentage"];
            if (data is null) return null;
            var next = new System.Collections.Generic.Dictionary<string, CounterSample>();
            double sum = 0; bool any = false;
            foreach (InstanceData d in data.Values)
            {
                var name = d.InstanceName;
                if (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) || !name.Contains(luid, StringComparison.OrdinalIgnoreCase)) continue;
                next[name] = d.Sample;
                if (_prevEng.TryGetValue(name, out var prev)) { sum += CounterSample.Calculate(prev, d.Sample); any = true; }
            }
            _prevEng = next;
            return any ? Math.Clamp(sum, 0, 100) : (next.Count > 0 ? null : 0);
        }
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
        foreach (var c in _vram ?? Array.Empty<PerformanceCounter>()) c.Dispose();
    }
}
