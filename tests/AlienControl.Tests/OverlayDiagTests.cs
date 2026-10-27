using System.Diagnostics;
using AlienControl.Services;
using Xunit;

namespace AlienControl.Tests;

/// <summary>diag (overlay latency experiment): the A/B Playhead modes and the --qpc_time columns. Adaptive must stay
/// the 2.0.2 behaviour; the diag modes only run with overlay-diag.json.</summary>
public class OverlayDiagTests
{
    private static FramesSource.Frame[] Burst()
    {
        long f = Stopwatch.Frequency;
        var fr = new FramesSource.Frame[120];
        for (int i = 0; i < 120; i++)
        {
            double t = 10 + i / 60.0;
            long arrive = (long)((i < 60 ? t : 10 + 119 / 60.0) * f);
            fr[i] = new FramesSource.Frame(arrive, t, 16.7f);
        }
        return fr;
    }

    [Fact]
    public void Default_mode_is_adaptive()
    {
        Assert.Equal(OverlayDiag.PlayMode.Adaptive, new FramesSource.Playhead().Mode);
        Assert.False(OverlayDiag.On);   // no overlay-diag.json on the test machine
    }

    [Fact]
    public void Off_puts_the_newest_frame_at_the_edge()
    {
        var fr = Burst();
        var ph = new FramesSource.Playhead { Mode = OverlayDiag.PlayMode.Off };
        Assert.Equal(120, ph.Visible(fr, fr[119].Ticks));
        Assert.Equal(fr[119].T, ph.Now);
        Assert.Equal(0, ph.Delay);
    }

    [Fact]
    public void Fixed_uses_the_constant_hold()
    {
        var fr = Burst();
        var ph = new FramesSource.Playhead { Mode = OverlayDiag.PlayMode.Fixed, FixedSec = 0.05 };
        ph.Visible(fr, fr[119].Ticks);
        Assert.Equal(0.05, ph.Delay, 6);
        var ad = new FramesSource.Playhead();
        ad.Visible(fr, fr[119].Ticks);
        Assert.True(ad.Delay > 0.5);   // adaptive grows to the burst
    }

    [Fact]
    public void Qpc_columns_are_parsed_on_stopwatch_clock()
    {
        long f = Stopwatch.Frequency;
        var s = new FramesSource();
        Assert.True(s.ParseHeader("Application,ProcessID,SwapChainAddress,PresentRuntime,TimeInQPC,MsBetweenPresents,CPUStartQPC"));
        s.SetForegroundForTest(5);
        long c = 123 * f, p = c + f / 100;
        Assert.True(s.OnLine($"game.exe,5,0x1,DXGI,{p},16.6,{c}"));
        var buf = new FramesSource.Frame[2];
        Assert.Equal(1, s.CopyRecent(buf));
        Assert.Equal(123.0, buf[0].T, 6);
        Assert.Equal(0, buf[0].Pq);   // Pq is only parsed with diag on
    }

    // ---------- diag pass 2 ----------

    [Fact]
    public void Pump_stamps_the_chunk_each_line_came_from()
    {
        var pump = new OverlayDiag.Pump();
        var enc = System.Text.Encoding.ASCII;
        var a = enc.GetBytes("Application,ProcessID\nrow1\nrow2\n");
        var b = enc.GetBytes("row3\n");
        pump.Push(a, a.Length, 1000);
        pump.Push(b, b.Length, 5000);
        pump.Complete();
        var rd = new System.IO.StreamReader(pump, enc, false, 4096);
        Assert.Equal("Application,ProcessID", rd.ReadLine());
        Assert.Equal("row1", rd.ReadLine());
        Assert.Equal(1000, pump.LastQpc);
        Assert.Equal("row2", rd.ReadLine());
        Assert.Equal("row3", rd.ReadLine());
        Assert.Equal(5000, pump.LastQpc);
        Assert.Equal(2, pump.LastSeq);
        Assert.Null(rd.ReadLine());
    }

    [Fact]
    public void Etw_status_line_is_parsed()
    {
        Assert.True(OverlayDiag.TryParseEtwStatus("[ETW Status] BufferFillPct=12.5% BuffersInUse=32 TotalBuffers=256 EventsLost=7 BuffersLost=1, OverflowedPresents=3", out var e));
        Assert.Equal(12.5, e.FillPct, 3);
        Assert.Equal(32, e.InUse);
        Assert.Equal(256, e.Total);
        Assert.Equal(7, e.EventsLost);
        Assert.Equal(1, e.BuffersLost);
        Assert.Equal(3, e.Overflowed);
        Assert.False(OverlayDiag.TryParseEtwStatus("Castlevania.exe,1234,0x1,DXGI", out _));
    }

    [Fact]
    public void Flush_schedule_baseline_fixed_and_cycle()
    {
        long f = Stopwatch.Frequency, st = 0;
        var c = new OverlayDiag.Config();
        Assert.Equal(0, OverlayDiag.FlushMsAt(c, 1, ref st));           // default: no explicit flush
        c.Flush = "100";
        Assert.Equal(100, OverlayDiag.FlushMsAt(c, 1, ref st));
        c.Flush = "ab"; c.FlushSeconds = 30; st = 0;
        long t0 = 10 * f;
        Assert.Equal(0, OverlayDiag.FlushMsAt(c, t0, ref st));
        Assert.Equal(250, OverlayDiag.FlushMsAt(c, t0 + 31 * f, ref st));
        Assert.Equal(100, OverlayDiag.FlushMsAt(c, t0 + 61 * f, ref st));
        Assert.Equal(50, OverlayDiag.FlushMsAt(c, t0 + 91 * f, ref st));
        Assert.Equal(0, OverlayDiag.FlushMsAt(c, t0 + 121 * f, ref st));
        Assert.Equal("off flush50", OverlayDiag.Label(OverlayDiag.PlayMode.Off, 50));
        Assert.Equal("adaptive noflush", OverlayDiag.Label(OverlayDiag.PlayMode.Adaptive, 0));
    }

    [Fact]
    public void Drain_schedule_stock_fixed_and_cycle()
    {
        long f = Stopwatch.Frequency, st = 0;
        var c = new OverlayDiag.Config();
        Assert.False(c.Patched);
        Assert.Equal(100, OverlayDiag.DrainMsAt(c, 1, ref st));         // default: stock Sleep(100)
        c.Drain = "16";
        Assert.Equal(16, OverlayDiag.DrainMsAt(c, 1, ref st));
        c.Drain = "0";
        Assert.Equal(100, OverlayDiag.DrainMsAt(c, 1, ref st));
        c.Drain = "ab"; c.DrainSeconds = 30; st = 0;
        long t0 = 10 * f;
        Assert.Equal(100, OverlayDiag.DrainMsAt(c, t0, ref st));
        Assert.Equal(16, OverlayDiag.DrainMsAt(c, t0 + 31 * f, ref st));
        Assert.Equal(10, OverlayDiag.DrainMsAt(c, t0 + 61 * f, ref st));
        Assert.Equal(100, OverlayDiag.DrainMsAt(c, t0 + 91 * f, ref st));
        c.PresentMon = "Patched";
        Assert.True(c.Patched);
    }

    [Fact]
    public void Trace_properties_offsets_match_x64_layout()
    {
        var p = System.Runtime.InteropServices.Marshal.AllocHGlobal(OverlayDiag.PropsSize);
        try
        {
            for (int i = 0; i < OverlayDiag.PropsSize; i += 4) System.Runtime.InteropServices.Marshal.WriteInt32(p, i, i);
            var q = OverlayDiag.ReadProps(0, p);
            Assert.Equal(48u, q.BufferKb); Assert.Equal(64u, q.LogFileMode); Assert.Equal(68u, q.FlushTimer);
            Assert.Equal(88u, q.EventsLost); Assert.Equal(100u, q.RealTimeBuffersLost);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(p); }
    }
}
