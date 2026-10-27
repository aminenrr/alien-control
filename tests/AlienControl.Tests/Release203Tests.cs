using System;
using System.Diagnostics;
using AlienControl.Services;
using Xunit;

namespace AlienControl.Tests;

/// <summary>2.0.3: GPU USE source selection, the NvAPI pstates buffer, the ETW flush schedule and the fixed Playhead.</summary>
public class Release203Tests
{
    private static byte[] Pstates(uint version, uint flags, uint present0, uint pct0)
    {
        var b = new byte[NvDrs.PstatesInfoSize];
        BitConverter.GetBytes(version).CopyTo(b, 0);
        BitConverter.GetBytes(flags).CopyTo(b, 4);
        BitConverter.GetBytes(present0).CopyTo(b, 8);
        BitConverter.GetBytes(pct0).CopyTo(b, 12);
        // frame buffer domain (index 1), must not be taken
        BitConverter.GetBytes(1u).CopyTo(b, 16);
        BitConverter.GetBytes(13u).CopyTo(b, 20);
        return b;
    }

    [Fact]
    public void Pstates_struct_is_version_1_of_72_bytes()
    {
        Assert.Equal(72, NvDrs.PstatesInfoSize);
        Assert.Equal(0x10048u, NvDrs.PstatesInfoVer);
    }

    [Fact]
    public void Graphics_utilization_parsed_from_domain_0()
    {
        Assert.Equal(87, NvDrs.ParseGraphicsUtil(Pstates(NvDrs.PstatesInfoVer, 1, 1, 87)));
        Assert.Equal(87, NvDrs.ParseGraphicsUtil(Pstates(NvDrs.PstatesInfoVer, 0, 1, 87)));   // flags bit not required
        Assert.Equal(0, NvDrs.ParseGraphicsUtil(Pstates(NvDrs.PstatesInfoVer, 1, 1, 0)));
    }

    [Fact]
    public void Graphics_utilization_rejects_bad_buffers()
    {
        Assert.Null(NvDrs.ParseGraphicsUtil(Pstates(NvDrs.PstatesInfoVer, 1, 0, 87)));        // domain not present
        Assert.Null(NvDrs.ParseGraphicsUtil(Pstates(NvDrs.PstatesInfoVer, 1, 1, 101)));       // out of range
        Assert.Null(NvDrs.ParseGraphicsUtil(Pstates(0x20048u, 1, 1, 50)));                    // other version
        Assert.Null(NvDrs.ParseGraphicsUtil(new byte[40]));                                   // short
    }

    [Fact]
    public void Driver_only_asked_with_the_rtx_on_and_awake()
    {
        Assert.True(GpuUse.MayAskDriver(GpuModeKind.DGpuOnly, false, false, false, null));
        Assert.True(GpuUse.MayAskDriver(GpuModeKind.Ultimate, false, false, false, 0));
        Assert.True(GpuUse.MayAskDriver(GpuModeKind.Optimus, false, false, false, 40));
        Assert.False(GpuUse.MayAskDriver(GpuModeKind.Optimus, false, false, false, 0));      // may be asleep
        Assert.False(GpuUse.MayAskDriver(GpuModeKind.Optimus, false, false, false, null));
        Assert.False(GpuUse.MayAskDriver(GpuModeKind.IGpuOnly, false, false, false, 40));
        Assert.False(GpuUse.MayAskDriver(GpuModeKind.Unknown, false, false, false, 40));
        Assert.False(GpuUse.MayAskDriver(GpuModeKind.DGpuOnly, true, false, false, 40));     // switching
        Assert.False(GpuUse.MayAskDriver(GpuModeKind.DGpuOnly, false, true, false, 40));     // source unsure
        Assert.False(GpuUse.MayAskDriver(GpuModeKind.DGpuOnly, false, false, true, 40));     // device removal
    }

    [Fact]
    public void Driver_value_wins_and_pdh_is_the_fallback()
    {
        Assert.Equal(92, GpuUse.Pick(92, 78));
        Assert.Equal(78, GpuUse.Pick(null, 78));
        Assert.Null(GpuUse.Pick(null, null));
        Assert.Equal(100, GpuUse.Pick(130, 78));
    }

    [Fact]
    public void Flush_schedule_keeps_the_period_and_never_bursts()
    {
        Assert.Equal(150, EtwFlush.NextDue(100, 120, 50));   // on time: one period on
        Assert.Equal(450, EtwFlush.NextDue(100, 400, 50));   // fell behind: re-anchored from now
        long f = 10_000_000;
        Assert.Equal(50, EtwFlush.SleepMs(f / 20, 0, f, 50));
        Assert.Equal(0, EtwFlush.SleepMs(0, 10, f, 50));
        Assert.Equal(50, EtwFlush.SleepMs(f, 0, f, 50));     // capped at one period
    }

    [Fact]
    public void Playhead_is_fixed_180_ms_by_default()
    {
        var ph = new FramesSource.Playhead();
        Assert.False(ph.Adaptive);
        Assert.Equal(0.18, ph.FixedSec, 6);
        long f = Stopwatch.Frequency;
        // 100 fps arriving on time, then one frame 1 s late: adaptive would grow its delay to ~1 s, fixed stays 180 ms
        var fr = new FramesSource.Frame[200];
        for (int i = 0; i < 200; i++)
        {
            double t = 10 + i / 100.0;
            fr[i] = new FramesSource.Frame((long)((i == 150 ? t + 1.0 : t) * f), t, 10f);
        }
        long now = (long)(fr[199].T * f);
        ph.Visible(fr, now);
        Assert.Equal(0.18, ph.Hold, 6);
        Assert.Equal(fr[199].T - 0.18, ph.Now, 3);
        var ad = new FramesSource.Playhead { Adaptive = true };
        ad.Visible(fr, now);
        Assert.True(ad.Hold > 0.9);
    }
}
