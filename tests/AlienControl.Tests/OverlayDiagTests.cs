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
}
