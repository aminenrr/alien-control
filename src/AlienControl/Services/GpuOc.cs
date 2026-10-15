using System;
using System.Runtime.InteropServices;

namespace AlienControl.Services;

/// <summary>
/// v0.9.7 GPU overclock through NVML clock offsets (nvml.dll ships with the NVIDIA driver, no extra install).
/// Probe on the m15 R7, driver 617.42 (dev/oc-probe.ps1): the legacy *VfOffset calls are Not Supported,
/// nvmlDeviceGet/SetClockOffsets works (graphics -1000..1000, memory -2000..6000). Calibrated in Control:
/// the core offset is 1:1, the memory offset counts double (NVML +200 = +100 MHz on the clock tools show),
/// so the UI keeps AWCC-style MHz and memory is sent x2. NVML is opened for each call and closed right after:
/// nothing stays loaded, no idle cost. Needs admin (the app already runs elevated).
/// </summary>
public static class GpuOc
{
    /// <summary>UI limit for both sliders, like AWCC.</summary>
    public const int MaxUi = 300;

    private const int ClockGraphics = 0, ClockMem = 2, PState0 = 0;

    // nvmlClockOffset_v1: version, type, pstate, clockOffsetMHz, minClockOffsetMHz, maxClockOffsetMHz (24 bytes).
    [StructLayout(LayoutKind.Sequential)]
    private struct ClockOffset { public uint Version; public int Type; public int PState; public int Cur; public int Min; public int Max; }
    private static readonly uint Ver1 = (uint)Marshal.SizeOf<ClockOffset>() | (1u << 24);

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockOffsets(IntPtr device, ref ClockOffset o);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceSetClockOffsets(IntPtr device, ref ClockOffset o);

    private static readonly object Gate = new();

    /// <summary>Sets core / memory offsets in AWCC-style MHz (0..300). Null = done, otherwise a short error.
    /// If only one of the two goes through, both are put back to 0.</summary>
    public static string? Set(int coreMhz, int memMhz)
    {
        coreMhz = Math.Clamp(coreMhz, 0, MaxUi);
        memMhz = Math.Clamp(memMhz, 0, MaxUi);
        lock (Gate)
        {
            try
            {
                int r = nvmlInit_v2();
                if (r != 0) return $"GPU OC: NVIDIA driver not ready (NVML {r}).";
                try
                {
                    r = nvmlDeviceGetHandleByIndex_v2(0, out var dev);
                    if (r != 0) return $"GPU OC: RTX not found (NVML {r}).";
                    var err = Write(dev, ClockGraphics, coreMhz) ?? Write(dev, ClockMem, memMhz * 2);
                    if (err is not null && (coreMhz != 0 || memMhz != 0))
                    {
                        Write(dev, ClockGraphics, 0);
                        Write(dev, ClockMem, 0);
                    }
                    return err;
                }
                finally { nvmlShutdown(); }
            }
            catch (DllNotFoundException) { return "GPU OC: nvml.dll not found (NVIDIA driver missing)."; }
            catch (EntryPointNotFoundException) { return "GPU OC: this NVIDIA driver is too old for clock offsets."; }
            catch (Exception ex) { return "GPU OC: " + ex.Message; }
        }
    }

    /// <summary>Reads the offset first so the driver's own range is checked before anything is written.</summary>
    private static string? Write(IntPtr dev, int type, int nvmlMhz)
    {
        string name = type == ClockGraphics ? "core" : "memory";
        var o = new ClockOffset { Version = Ver1, Type = type, PState = PState0 };
        int r = nvmlDeviceGetClockOffsets(dev, ref o);
        if (r != 0) return $"GPU OC: can't read the {name} offset (NVML {r}).";
        if (nvmlMhz < o.Min || nvmlMhz > o.Max) return $"GPU OC: {name} offset out of the driver's range ({o.Min}..{o.Max}).";
        if (o.Cur == nvmlMhz) return null;
        o.Version = Ver1; o.Type = type; o.PState = PState0; o.Cur = nvmlMhz;
        r = nvmlDeviceSetClockOffsets(dev, ref o);
        return r == 0 ? null : $"GPU OC: driver refused the {name} offset (NVML {r}).";
    }
}
