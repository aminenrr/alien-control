using System;
using System.Runtime.InteropServices;

namespace AlienControl.Services;

/// <summary>
/// v0.9.7 GPU overclock through NVML clock offsets (nvml.dll ships with the NVIDIA driver, no extra install).
/// Probe on the m15 R7, driver 617.42 (dev/oc-probe.ps1): the legacy *VfOffset calls are Not Supported,
/// nvmlDeviceGet/SetClockOffsets works (graphics -1000..1000, memory -2000..6000). Calibrated in Control:
/// the core offset is 1:1, the memory offset counts double (NVML +200 = +100 MHz on the clock tools show),
/// so the UI keeps AWCC-style MHz and memory is sent x2. NVML is opened for each call and closed right after
/// (2.0.0-beta1.3: unless the power meter keeps the shared session open): nothing stays loaded, no idle cost. Needs admin (the app already runs elevated).
/// </summary>
public static class GpuOc
{
    /// <summary>2.0.2 (review, Amine): whether the exit cleanup may clear a test's OcPending marker. Only when the +0
    /// write went through, or when the app reliably knows the driver is at +0 (last confirmed write or read was +0,
    /// nothing non-zero in flight, not waiting on a driver check). A skipped reset (RTX off or switching) keeps it.</summary>
    internal static bool ExitClearsPending(bool resetOk, bool mayBeOn, bool unverified) =>
        resetOk || (!mayBeOn && !unverified);

    /// <summary>2.0.2 (review, Amine): startup recovery of a pending test is done only when the +0 write ran and
    /// worked. RTX disabled = skipped, not done.</summary>
    internal static bool StartupRecovered(bool rtxOff, string? err) => !rtxOff && err is null;

    /// <summary>2.0.2 (review, Amine): a pending recovery (startup, revert / timeout, failed apply) is done once the
    /// driver is confirmed at the state the app intends outside a test: +0, or the kept offsets when a failed apply
    /// went back to them. During a test OcPending belongs to the test.</summary>
    internal static bool PendingRecoveryDone(bool pending, bool testing, (int Core, int Mem)? applied, (int Core, int Mem) want) =>
        pending && !testing && applied is { } a && a == want;

    /// <summary>2.0.0-beta1.4 (Amine, bug B): false when nvml.dll isn't in System32 (no NVIDIA driver), so the OC card
    /// is greyed instead of failing on Apply.</summary>
    public static bool NvmlPresent => System.IO.File.Exists(System.IO.Path.Combine(Environment.SystemDirectory, "nvml.dll"));

    /// <summary>UI limit for both sliders, like AWCC.</summary>
    public const int MaxUi = 300;

    private const int ClockGraphics = 0, ClockMem = 2, PState0 = 0;

    // nvmlClockOffset_v1: version, type, pstate, clockOffsetMHz, minClockOffsetMHz, maxClockOffsetMHz (24 bytes).
    private static readonly uint Ver1 = (uint)Marshal.SizeOf<Nvml.ClockOffset>() | (1u << 24);


    /// <summary>Sets core / memory offsets in AWCC-style MHz (0..300). Null = done, otherwise a short error.
    /// If only one of the two goes through, both are put back to 0.
    /// 2.0.0-beta1.3: runs through the shared NvmlSession (one lock with the power meter, refused while the RTX is
    /// being removed). lockTimeoutMs bounds the wait for the crash / logoff paths.</summary>
    public static string? Set(int coreMhz, int memMhz, int lockTimeoutMs = System.Threading.Timeout.Infinite)
    {
        coreMhz = Math.Clamp(coreMhz, 0, MaxUi);
        memMhz = Math.Clamp(memMhz, 0, MaxUi);
        try
        {
            return NvmlSession.Run(false, dev =>
            {
                var err = Write(dev, ClockGraphics, coreMhz) ?? Write(dev, ClockMem, memMhz * 2);
                if (err is not null && (coreMhz != 0 || memMhz != 0))
                {
                    Write(dev, ClockGraphics, 0);
                    Write(dev, ClockMem, 0);
                }
                return err;
            }, Failed, lockTimeoutMs);
        }
        catch (DllNotFoundException) { return "GPU OC: nvml.dll not found (NVIDIA driver missing)."; }
        catch (EntryPointNotFoundException) { return "GPU OC: this NVIDIA driver is too old for clock offsets."; }
        catch (Exception ex) { return "GPU OC: " + ex.Message; }
    }

    private static string? Failed(NvmlSession.Fail f, int r) => f switch
    {
        NvmlSession.Fail.Init => $"GPU OC: NVIDIA driver not ready (NVML {r}).",
        NvmlSession.Fail.NoDevice => $"GPU OC: RTX not found (NVML {r}).",
        NvmlSession.Fail.Removing => "GPU OC: the RTX is being switched, try again once it's done.",
        _ => "GPU OC: NVIDIA driver busy.",
    };

    /// <summary>2.0.0-beta1.3 (services review #2): the offsets the driver runs now, in the UI's MHz (memory / 2), read
    /// with the same nvmlDeviceGetClockOffsets call Write checks the range with. Null = couldn't read (no NVIDIA driver,
    /// RTX off or being switched), or a value the app never writes (odd memory offset), so the caller can't trust it.</summary>
    public static (int Core, int Mem)? Read()
    {
        try
        {
            return NvmlSession.Run<(int, int)?>(false, dev =>
            {
                if (ReadCur(dev, ClockGraphics) is not { } core || ReadCur(dev, ClockMem) is not { } mem) return null;
                if (mem % 2 != 0) return null;
                return (core, mem / 2);
            }, (_, _) => null);
        }
        catch (Exception ex) { Log.Warn("GpuOc.Read", ex); return null; }
    }

    private static int? ReadCur(IntPtr dev, int type)
    {
        var o = new Nvml.ClockOffset { Version = Ver1, Type = type, PState = PState0 };
        return Nvml.nvmlDeviceGetClockOffsets(dev, ref o) == 0 ? o.Cur : null;
    }

    /// <summary>Reads the offset first so the driver's own range is checked before anything is written.</summary>
    private static string? Write(IntPtr dev, int type, int nvmlMhz)
    {
        string name = type == ClockGraphics ? "core" : "memory";
        var o = new Nvml.ClockOffset { Version = Ver1, Type = type, PState = PState0 };
        int r = Nvml.nvmlDeviceGetClockOffsets(dev, ref o);
        if (r != 0) return $"GPU OC: can't read the {name} offset (NVML {r}).";
        if (nvmlMhz < o.Min || nvmlMhz > o.Max) return $"GPU OC: {name} offset out of the driver's range ({o.Min}..{o.Max}).";
        if (o.Cur == nvmlMhz) return null;
        o.Version = Ver1; o.Type = type; o.PState = PState0; o.Cur = nvmlMhz;
        r = Nvml.nvmlDeviceSetClockOffsets(dev, ref o);
        return r == 0 ? null : $"GPU OC: driver refused the {name} offset (NVML {r}).";
    }
}
