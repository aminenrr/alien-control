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
            double used = _vram.Max(c => { try { return (double)c.RawValue; } catch { return 0d; } });
            const double gb = 1024d * 1024 * 1024;
            double total = _vramTotal > 0 ? _vramTotal / gb : 0;
            return (used / gb, total);
        }
        catch { return null; }
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
