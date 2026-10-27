using System;

namespace AlienControl.Services;

/// <summary>2.0.3 (Amine: GPU USE read 10 to 15 points below RTSS): which reading the RTX GPU USE shows. The driver's
/// graphics load (NvAPI, what RTSS shows) when the RTX is on and known awake; otherwise, or when the driver call
/// fails, the PDH engtype_3D sum as before.</summary>
public static class GpuUse
{
    /// <summary>PDH 3D use (%) above which the RTX is taken as awake on Optimus: it is doing 3D work right now, so a
    /// driver query can't be what wakes it.</summary>
    public const double AwakeAbovePct = 0.5;

    /// <summary>May the driver be asked? Never with the RTX off or mid switch, or while the GPU tiles' source isn't
    /// known yet. dGPU only / Ultimate: the RTX drives the panel, always awake. Optimus: only while PDH sees it busy.
    /// Unknown mode: no (the RTX might be asleep).</summary>
    public static bool MayAskDriver(GpuModeKind kind, bool switching, bool sourceUnsure, bool removing, double? pdh)
    {
        if (switching || sourceUnsure || removing) return false;
        return kind switch
        {
            GpuModeKind.DGpuOnly or GpuModeKind.Ultimate => true,
            GpuModeKind.Optimus => pdh is double p && p > AwakeAbovePct,
            _ => false,
        };
    }

    /// <summary>The value to show: the driver's when it gave one, else PDH (null stays null).</summary>
    public static double? Pick(double? driver, double? pdh) => driver is double d ? Math.Clamp(d, 0, 100) : pdh;
}
