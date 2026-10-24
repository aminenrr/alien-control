using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AlienControl.Services;

/// <summary>
/// beta1.3-ov: per core load and real per core clock for the overlay, read only while a level shows them.
///
/// Clock: Windows' "% Processor Performance" counter per logical processor times the base clock, the same effective
/// frequency Task Manager shows (CallNtPowerInformation only reports the base clock, 2300 MHz on the i7-12700H).
/// One PDH query with a wildcard, one collect per second, no driver, no MSR access.
/// Topology: GetLogicalProcessorInformationEx tells P cores from E cores (EfficiencyClass) and which threads share a
/// core, so the overlay shows one row per physical core (P1..P6, E1..E8 on the 12700H).
/// Its own "since last call" state, so it never disturbs the window's CPU % reader.
/// </summary>
public sealed class CpuCores : IDisposable
{
    /// <summary>One physical core: P (performance), E (efficiency) or C (non hybrid CPU), its threads (logical ids).</summary>
    public sealed record Core(char Kind, int Index, int[] Threads);

    /// <summary>One row for the overlay: load per thread of the core (0..100) and the core's clock in MHz.</summary>
    public readonly record struct CoreReading(char Kind, int Index, double[] Loads, int? Mhz);

    private readonly int _n = Environment.ProcessorCount;
    private SPPI[] _prev = Array.Empty<SPPI>();
    private IReadOnlyList<Core>? _cores;
    private IntPtr _query, _counter;
    private bool _pdhFailed;
    private int _baseMhz;

    public IReadOnlyList<Core> Topology => _cores ??= ReadTopology(_n);

    // ---------- load per logical processor ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct SPPI { public long Idle, Kernel, User, Dpc, Interrupt; public uint InterruptCount; }   // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int cls, [Out] SPPI[] info, int len, out int ret);

    /// <summary>Load per logical processor, 0..100, since the last call. Null on the first call.</summary>
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

    // ---------- effective clock per logical processor (PDH) ----------

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double Double; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr Name; public PDH_FMT_COUNTERVALUE Value; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? src, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint fmt, ref uint bytes, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    private const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;

    /// <summary>Effective MHz per logical processor (index = logical id). Null on the first call or when PDH fails.</summary>
    public int[]? Mhz()
    {
        if (_pdhFailed) return null;
        try
        {
            if (_query == IntPtr.Zero)
            {
                _baseMhz = BaseMhz();
                if (_baseMhz <= 0 || PdhOpenQuery(null, IntPtr.Zero, out _query) != 0
                    || PdhAddEnglishCounter(_query, @"\Processor Information(*)\% Processor Performance", IntPtr.Zero, out _counter) != 0)
                { Fail("PDH query"); return null; }
                PdhCollectQueryData(_query);   // rate counter: the first collect is the baseline
                return null;
            }
            if (PdhCollectQueryData(_query) != 0) return null;
            uint bytes = 0;
            uint st = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bytes, out uint count, IntPtr.Zero);
            if (st != PDH_MORE_DATA || bytes == 0) return null;
            var buf = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bytes, out count, buf) != 0) return null;
                var mhz = new int[_n];
                int size = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                for (int i = 0; i < count; i++)
                {
                    var it = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * size);
                    var name = Marshal.PtrToStringUni(it.Name) ?? "";
                    // instances are "group,number" (plus "_Total" and "0,_Total")
                    int comma = name.IndexOf(',');
                    if (comma < 0 || !int.TryParse(name.AsSpan(0, comma), out int group) || !int.TryParse(name.AsSpan(comma + 1), out int num)) continue;
                    int id = group * 64 + num;
                    if (id < 0 || id >= _n || it.Value.CStatus > 1) continue;
                    mhz[id] = (int)Math.Round(_baseMhz * it.Value.Double / 100.0);
                }
                return mhz;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception e) { Fail(e.Message); return null; }
    }

    private void Fail(string why)
    {
        _pdhFailed = true;
        Log.Info("CpuCores", "per core clock unavailable: " + why);
        Close();
    }

    /// <summary>Base clock from the registry (~MHz, what the % Processor Performance counter is relative to).</summary>
    private static int BaseMhz()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return k?.GetValue("~MHz") is int v ? v : 0;
        }
        catch { return 0; }
    }

    /// <summary>One row per physical core: thread loads and the core's clock (its fastest thread).</summary>
    public static CoreReading[] Combine(IReadOnlyList<Core> cores, double[]? loads, int[]? mhz)
    {
        var r = new CoreReading[cores.Count];
        for (int c = 0; c < cores.Count; c++)
        {
            var core = cores[c];
            var l = new double[core.Threads.Length];
            int max = 0;
            for (int t = 0; t < core.Threads.Length; t++)
            {
                int id = core.Threads[t];
                if (loads is not null && id < loads.Length) l[t] = loads[id];
                if (mhz is not null && id < mhz.Length) max = Math.Max(max, mhz[id]);
            }
            r[c] = new CoreReading(core.Kind, core.Index, l, max > 0 ? max : null);
        }
        return r;
    }

    /// <summary>The highest core clock (what MangoHud shows on the CPU row).</summary>
    public static int? MaxMhz(int[]? mhz)
    {
        if (mhz is null) return null;
        int m = 0;
        foreach (var x in mhz) m = Math.Max(m, x);
        return m > 0 ? m : null;
    }

    // ---------- topology ----------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relation, IntPtr buffer, ref uint length);

    /// <summary>P cores first, then E cores, each numbered from 1 in Windows' order.</summary>
    internal static IReadOnlyList<Core> ReadTopology(int logical)
    {
        var raw = new List<(byte Eff, int[] Threads)>();
        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);   // RelationProcessorCore
            if (len > 0)
            {
                var buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (GetLogicalProcessorInformationEx(0, buf, ref len))
                    {
                        int off = 0;
                        while (off < len)
                        {
                            var p = buf + off;
                            int size = Marshal.ReadInt32(p, 4);
                            // PROCESSOR_RELATIONSHIP at +8: Flags(1) EfficiencyClass(1) Reserved(20) GroupCount(2) GroupMask[]
                            byte eff = Marshal.ReadByte(p, 9);
                            ushort groups = (ushort)Marshal.ReadInt16(p, 30);
                            var threads = new List<int>();
                            for (int g = 0; g < groups; g++)
                            {
                                // GROUP_AFFINITY: KAFFINITY Mask (8), WORD Group, WORD Reserved[3]
                                var ga = p + 32 + g * 16;
                                ulong mask = (ulong)Marshal.ReadInt64(ga);
                                int group = Marshal.ReadInt16(ga, 8);
                                for (int b = 0; b < 64; b++) if ((mask & (1UL << b)) != 0) threads.Add(group * 64 + b);
                            }
                            if (threads.Count > 0) raw.Add((eff, threads.ToArray()));
                            if (size <= 0) break;
                            off += size;
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        catch (Exception e) { Log.Warn("CpuCores.Topology", e); }
        return Label(raw, logical);
    }

    /// <summary>Highest efficiency class = P cores. One class only = a non hybrid CPU (C1, C2, ...).</summary>
    internal static IReadOnlyList<Core> Label(List<(byte Eff, int[] Threads)> raw, int logical)
    {
        var cores = new List<Core>();
        if (raw.Count == 0)
        {
            for (int i = 0; i < logical; i++) cores.Add(new Core('C', i + 1, new[] { i }));
            return cores;
        }
        byte max = 0, min = 255;
        foreach (var r in raw) { max = Math.Max(max, r.Eff); min = Math.Min(min, r.Eff); }
        bool hybrid = max != min;
        int p = 0, e = 0;
        foreach (var r in raw) if (!hybrid || r.Eff == max) cores.Add(new Core(hybrid ? 'P' : 'C', ++p, r.Threads));
        if (hybrid) foreach (var r in raw) if (r.Eff != max) cores.Add(new Core('E', ++e, r.Threads));
        return cores;
    }

    private void Close()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; _counter = IntPtr.Zero; }
    }

    /// <summary>Called when the overlay stops showing clocks: frees the PDH query (reopened on demand).</summary>
    public void Release() => Close();

    public void Dispose() => Close();
}
