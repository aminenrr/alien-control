using System;
using System.Runtime.InteropServices;

namespace AlienControl.Services;

/// <summary>
/// beta1.3-ov: per core load (overlay level 4) and the CPU clock (levels 3 and 4), read only while the overlay shows
/// them. Its own "since last call" state, so it never disturbs the window's CPU % reader.
/// </summary>
public sealed class CpuCores
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SPPI { public long Idle, Kernel, User, Dpc, Interrupt; public uint InterruptCount; }   // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION

    [StructLayout(LayoutKind.Sequential)]
    private struct PPI { public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState; }   // PROCESSOR_POWER_INFORMATION

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int cls, [Out] SPPI[] info, int len, out int ret);
    [DllImport("powrprof.dll")] private static extern int CallNtPowerInformation(int level, IntPtr inBuf, int inLen, [Out] PPI[] outBuf, int outLen);

    private readonly int _n = Environment.ProcessorCount;
    private SPPI[] _prev = Array.Empty<SPPI>();

    /// <summary>Load per logical core, 0..100, since the last call. Null on the first call.</summary>
    public double[]? Loads()
    {
        try
        {
            var cur = new SPPI[_n];
            if (NtQuerySystemInformation(8, cur, Marshal.SizeOf<SPPI>() * _n, out _) != 0) return null;
            var prev = _prev;
            _prev = cur;
            if (prev.Length != _n) return null;
            var r = new double[_n];
            for (int i = 0; i < _n; i++)
            {
                long idle = cur[i].Idle - prev[i].Idle;
                long total = (cur[i].Kernel - prev[i].Kernel) + (cur[i].User - prev[i].User);   // kernel includes idle
                r[i] = total <= 0 ? 0 : Math.Clamp((total - idle) * 100.0 / total, 0, 100);
            }
            return r;
        }
        catch (Exception e) { Log.Warn("CpuCores.Loads", e); return null; }
    }

    /// <summary>Highest current core clock in MHz (what MangoHud shows as the CPU clock). Null when Windows won't say.</summary>
    public int? Mhz()
    {
        try
        {
            var buf = new PPI[_n];
            if (CallNtPowerInformation(11, IntPtr.Zero, 0, buf, Marshal.SizeOf<PPI>() * _n) != 0) return null;
            uint max = 0;
            foreach (var p in buf) max = Math.Max(max, p.CurrentMhz);
            return max == 0 ? null : (int)max;
        }
        catch (Exception e) { Log.Warn("CpuCores.Mhz", e); return null; }
    }
}
