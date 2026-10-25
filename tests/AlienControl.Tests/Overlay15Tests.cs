using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using AlienControl.Overlay;
using AlienControl.Services;
using Xunit;

namespace AlienControl.Tests;

/// <summary>ov.15: time-based graph, temp colours, clock / bound / stutter rows, Shift + F4, GPU tracking, renderer setting.</summary>
public class Overlay15Tests
{
    /// <summary>A canvas that measures with a fixed width per character and records what was drawn.</summary>
    private sealed class FakeCanvas : IOverlayCanvas
    {
        public readonly List<(string S, double X, double Y, Rgba C)> Texts = new();
        public readonly List<(double X, double Y, double W, double H, Rgba C)> Fills = new();
        public readonly List<double[]> Lines = new();
        public double TextWidth(string s, double size, bool bold) => s.Length * size * 0.6;
        public (double Height, double Baseline) LineMetrics(string s, double size, bool bold) => (size * 1.2, size);
        public void Text(string s, double x, double y, double size, bool bold, Rgba c) => Texts.Add((s, x, y, c));
        public void Fill(double x, double y, double w, double h, Rgba c) => Fills.Add((x, y, w, h, c));
        public void Polyline(ReadOnlySpan<double> pts, Rgba c, double width) => Lines.Add(pts.ToArray());
    }

    private static OverlayView View(int level, Item hidden = Item.None) =>
        new() { Level = level, Items = OverlayLevels.Items(level, false, hidden), Scale = 1 };

    [Fact]
    public void Cpu_and_gpu_temps_have_their_own_thresholds()
    {
        Assert.Equal(OverlayView.TempOk, OverlayView.CpuTempColor(79));
        Assert.Equal(OverlayView.TempWarm, OverlayView.CpuTempColor(80));
        Assert.Equal(OverlayView.TempWarm, OverlayView.CpuTempColor(89));
        Assert.Equal(OverlayView.TempHot, OverlayView.CpuTempColor(90));
        Assert.Equal(OverlayView.TempOk, OverlayView.GpuTempColor(74));
        Assert.Equal(OverlayView.TempWarm, OverlayView.GpuTempColor(75));
        Assert.Equal(OverlayView.TempWarm, OverlayView.GpuTempColor(79));
        Assert.Equal(OverlayView.TempHot, OverlayView.GpuTempColor(80));
        Assert.Equal(OverlayView.White, OverlayView.GpuTempColor(null));
        Assert.Equal(Rgba.Hex(0xFF3B3B), OverlayView.TempHot);   // the brighter red (Amine), not #B22222
    }

    [Fact]
    public void Level_2_shows_coloured_temps_in_the_strip()
    {
        var v = View(2);
        v.Data = new Sensors { CpuLoad = 41, CpuTemp = 84, CpuW = 38, GpuLoad = 72, GpuTemp = 83, GpuW = 118 };
        var items = v.BuildHorizontal();
        var cpu = items.Single(i => i.Label == "CPU");
        var gpu = items.Single(i => i.Label == "GPU");
        Assert.Equal(new[] { "41", "84", "38" }, cpu.Cells.Select(c => c.Value));
        Assert.Equal(OverlayView.TempWarm, cpu.Cells[1].Color);
        Assert.Equal(OverlayView.TempHot, gpu.Cells[1].Color);
        Assert.Equal(OverlayView.White, gpu.Cells[0].Color);
    }

    [Fact]
    public void Clock_and_bound_are_levels_3_and_4_and_off_by_default()
    {
        var hidden = OverlayLevels.Hidden(true, true, false, false);   // the defaults
        foreach (int lv in new[] { 1, 2 })
        {
            Assert.False(OverlayLevels.Items(lv).HasFlag(Item.Clock));
            Assert.False(OverlayLevels.Items(lv).HasFlag(Item.Bound));
        }
        foreach (int lv in new[] { 3, 4 })
        {
            Assert.True(OverlayLevels.Items(lv).HasFlag(Item.Clock));
            Assert.False(OverlayLevels.Items(lv, false, hidden).HasFlag(Item.Clock));
            Assert.False(OverlayLevels.Items(lv, false, hidden).HasFlag(Item.Bound));
            Assert.False(OverlayLevels.Items(lv).HasFlag(Item.Stutter));   // beta2: no stutter row
            Assert.False(OverlayLevels.Needs(lv, hidden).HasFlag(Need.GpuBusy));       // off = no GPU tracking
            Assert.True(OverlayLevels.Needs(lv, OverlayLevels.Hidden(true, true, true, true)).HasFlag(Need.GpuBusy));
        }
        var s = new Settings();
        Assert.False(s.OverlayShowClock);
        Assert.False(s.OverlayShowBound);
    }

    [Fact]
    public void Shift_f4_cycles_levels_and_f4_toggles()
    {
        Assert.Equal(0, OverlayLevels.OnKey(3, 3, shift: false));
        Assert.Equal(2, OverlayLevels.OnKey(0, 2, shift: false));
        Assert.Equal(1, OverlayLevels.OnKey(0, 3, shift: true));   // from off: level 1
        Assert.Equal(2, OverlayLevels.OnKey(1, 3, shift: true));
        Assert.Equal(4, OverlayLevels.OnKey(3, 3, shift: true));
        Assert.Equal(1, OverlayLevels.OnKey(4, 3, shift: true));   // wraps
    }

    [Fact]
    public void Gpu_tracking_only_when_asked()
    {
        Assert.Contains("--no_track_gpu", FramesSource.Args(false));
        Assert.DoesNotContain("--no_track_gpu", FramesSource.Args(false, trackGpu: true));
        Assert.Contains("--no_track_display", FramesSource.Args(false, trackGpu: true));
        Assert.Contains("--no_track_input", FramesSource.Args(true, trackGpu: true));
    }

    [Fact]
    public void Gpu_busy_and_sync_interval_are_parsed()
    {
        const string h = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,CPUStartTime,MsBetweenPresents,MsGPUBusy";
        var f = new FramesSource();
        Assert.True(f.ParseHeader(h));
        f.SetForegroundForTest(5);
        Assert.Equal(-1, f.SyncInterval);
        Assert.True(f.OnLine("game.exe,5,0x1,DXGI,1,1.0,6.9,6.5"));
        var buf = new FramesSource.Frame[4];
        Assert.Equal(1, f.CopyRecent(buf));
        Assert.Equal(6.5f, buf[0].GpuBusy, 3);
        Assert.Equal(1, f.SyncInterval);
        // no GPU column (tracking off): NaN, not 0
        var g = new FramesSource();
        g.ParseHeader("Application,ProcessID,SwapChainAddress,PresentRuntime,CPUStartTime,MsBetweenPresents");
        g.SetForegroundForTest(5);
        g.OnLine("game.exe,5,0x1,DXGI,1.0,6.9");
        g.CopyRecent(buf);
        Assert.True(float.IsNaN(buf[0].GpuBusy));
    }

    private static FramesSource.Frame[] Steady(int n, float ms, float gpu = float.NaN, double t0 = 0)
    {
        var a = new FramesSource.Frame[n];
        double t = t0;
        for (int i = 0; i < n; i++) { t += ms / 1000.0; a[i] = new(0, t, ms, GpuBusy: gpu); }
        return a;
    }

    [Fact]
    public void Bound_tells_gpu_cpu_and_limited()
    {
        Assert.Equal("GPU", FramesSource.Bound(Steady(300, 10f, 9.6f), 0));
        Assert.Equal("CPU", FramesSource.Bound(Steady(300, 10f, 5f), 0));
        Assert.Equal("LIMITED", FramesSource.Bound(Steady(300, 1000f / 60, 5f), 60));   // capped at 60
        Assert.Equal("CPU", FramesSource.Bound(Steady(300, 1000f / 45, 5f), 60));       // under the cap: not limited
        Assert.Equal("", FramesSource.Bound(Steady(300, 10f), 0));                      // no GPU busy yet
        var s = new Sensors { CapFps = 0, DriverVsync = false, ModeHz = 144 };
        Assert.Equal(0, s.LimitFps(0));
        Assert.Equal(144, s.LimitFps(1));                                   // the game's own V-Sync
        Assert.Equal(144, (s with { DriverVsync = true }).LimitFps(0));
        Assert.Equal(90, (s with { CapFps = 90, DriverVsync = true }).LimitFps(0));
    }

    [Fact]
    public void Graph_is_plotted_by_time_with_the_playhead_at_the_right_edge()
    {
        var v = View(3, OverlayLevels.Hidden(true, true, false, false));
        v.Data = new Sensors();
        int n = 300;
        var t = new double[n]; var ms = new float[n];
        for (int i = 0; i < n; i++) { t[i] = 10 + i * 0.01; ms[i] = 10f; }   // 3 s at 100 fps
        v.GraphT = t; v.Graph = ms; v.GraphCount = n; v.GraphNow = t[^1];
        var c = new FakeCanvas();
        var (w, _) = v.Measure(c);
        v.Render(c);
        var line = Assert.Single(c.Lines);
        double pad = 6, left = pad, right = w - pad;
        Assert.Equal(right, line[^2], 3);                    // newest frame on the right edge
        Assert.Equal(left, line[0], 3);                      // the first point is clamped to the left edge
        // a frame 1 s before the playhead sits half way (2 s window)
        int k = Array.FindIndex(t, x => Math.Abs(x - (t[^1] - 1.0)) < 1e-6);
        int firstDrawn = n - line.Length / 2;
        Assert.Equal(left + (right - left) / 2, line[(k - firstDrawn) * 2], 3);
        // moving the playhead by 10 ms moves every point left by the same sub pixel amount
        v.GraphNow += 0.01;
        var c2 = new FakeCanvas();
        v.Render(c2);
        double step = (right - left) * 0.01 / OverlayView.GraphSpan;
        Assert.Equal(line[^2] - step, c2.Lines[0][^2], 3);
    }


    [Fact]
    public void Bound_and_clock_rows()
    {
        var v = View(3, OverlayLevels.Hidden(true, true, true, true));
        v.Data = new Sensors();
        v.Clock = "23:41";
        v.Bound = "LIMITED";
        var rows = v.BuildRows();
        Assert.Equal("TIME", rows[0].Label);
        Assert.Equal("23:41", rows[0].Cells[0].Value);
        var b = rows.Single(r => r.Label == "BOUND");
        Assert.Equal("LIMITED", b.Cells[1].Value);
        Assert.Equal(OverlayView.TempWarm, b.Cells[1].Color);
        v.Bound = "GPU";
        b = v.BuildRows().Single(r => r.Label == "BOUND");
        Assert.Equal(new[] { "GPU", "EST" }, b.Cells.Select(x => x.Value));
        // BOUND sits right under the FPS row; beta2: no STUTTER row
        var labels = OverlayValuesWith(Item.Latency).BuildRows().Select(r => r.Label).ToList();
        Assert.Equal(labels.IndexOf("FPS") + 1, labels.IndexOf("BOUND"));
        Assert.DoesNotContain("STUTTER", labels);
    }

    private static OverlayView OverlayValuesWith(Item extra) =>
        new() { Level = 3, Items = OverlayLevels.L3 | extra, Scale = 1, Data = new Sensors(), Bound = "CPU" };

    [Fact]
    public void Renderer_setting_defaults_to_d2d_and_keeps_wpf()
    {
        Assert.Equal("d2d", new Settings().OverlayRenderer);
        var s = new Settings { OverlayRenderer = "wpf" };
        s.Clamp();
        Assert.Equal("wpf", s.OverlayRenderer);
        s.OverlayRenderer = "vulkan";
        s.Clamp();
        Assert.Equal("d2d", s.OverlayRenderer);
    }

    // beta2.1: the exact header PresentMon 2.6 printed on Amine's laptop (lean mode with --track_frame_type)
    [Fact]
    public void Ms_time_columns_and_unknown_frame_types_keep_real_frames()
    {
        const string h = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,FrameType,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,MsInPresentAPI,MsRenderPresentLatency,CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait";
        var f = new FramesSource();
        Assert.True(f.ParseHeader(h));
        f.SetForegroundForTest(9);
        double t = 1000;
        for (int i = 0; i < 40; i++)
        {
            t += 8.333;
            var tt = t.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(f.OnLine($"game.exe,9,0x1,DXGI,0,0,Unspecified,{tt},8.3,8.333,0.1,5.0,{tt},8.3,4.0,4.3"));
        }
        var buf = new FramesSource.Frame[64];
        int n = f.CopyRecent(buf);
        Assert.Equal(40, n);
        Assert.False(buf[0].Gen);
        Assert.Equal(0.008333, buf[1].T - buf[0].T, 5);   // ms column read as seconds
        var (fps, _) = FramesSource.FpsOver(buf.AsSpan(0, n), buf[n - 1].Ticks, System.Diagnostics.Stopwatch.Frequency / 4);
        Assert.InRange(fps, 118, 122);
    }

    [Fact]
    public void Only_frame_gen_types_count_as_generated()
    {
        Assert.False(FramesSource.IsGenerated("Unspecified"));
        Assert.False(FramesSource.IsGenerated("Application"));
        Assert.True(FramesSource.IsGenerated("AMD AFMF"));
        Assert.True(FramesSource.IsGenerated("Intel XeFG"));
        Assert.Contains("--set_circular_buffer_size 8192", FramesSource.Args(false));
    }

    // beta2.3: FG presents its pair back to back, the screen gets them evenly; the graph follows the screen
    [Fact]
    public void Display_change_is_the_frametime_when_tracked()
    {
        const string h = "Application,ProcessID,SwapChainAddress,PresentRuntime,FrameType,CPUStartTime,MsBetweenPresents,MsBetweenDisplayChange";
        var f = new FramesSource();
        Assert.True(f.ParseHeader(h));
        f.SetForegroundForTest(3);
        double t = 0;
        for (int i = 0; i < 100; i++)
        {
            float ms = i % 2 == 1 ? 0.3f : 13.7f;
            t += ms / 1000.0;
            var tt = t.ToString(System.Globalization.CultureInfo.InvariantCulture);
            f.OnLine($"game.exe,3,0x1,DXGI,Application,{tt},{ms.ToString(System.Globalization.CultureInfo.InvariantCulture)},7.0");
        }
        Assert.False(f.OnLine("game.exe,3,0x1,DXGI,Application,1.0,7.0,NA"));   // never reached the screen
        var buf = new FramesSource.Frame[128];
        int n = f.CopyRecent(buf);
        Assert.Equal(100, n);
        for (int i = 0; i < n; i++) Assert.Equal(7f, buf[i].Ms, 3);
    }
}
