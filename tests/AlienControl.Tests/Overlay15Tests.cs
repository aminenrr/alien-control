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
    public void Level_1_shows_coloured_temps_in_the_strip()
    {
        var v = View(1);   // beta3: the strip moved to level 1
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
        Assert.False(OverlayLevels.Items(1).HasFlag(Item.Clock));
        Assert.False(OverlayLevels.Items(1).HasFlag(Item.Bound));
        Assert.True(OverlayLevels.Items(2).HasFlag(Item.Clock));                 // beta3 Metal: clock on top, with its tile
        Assert.False(OverlayLevels.Items(2, false, hidden).HasFlag(Item.Clock));
        Assert.False(OverlayLevels.Items(2).HasFlag(Item.Bound));
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
    public void Graph_is_one_step_per_frame_and_slides_by_the_fraction()
    {
        var v = View(3, OverlayLevels.Hidden(true, true, false, false));
        v.Data = new Sensors();
        int n = 300;
        var t = new double[n]; var ms = new float[n];
        for (int i = 0; i < n; i++) { t[i] = 10 + i * 0.01; ms[i] = 10f; }
        ms[n - 50] = 250f;   // one hitch
        v.GraphT = t; v.Graph = ms; v.GraphCount = n; v.GraphFrac = 0;
        var c = new FakeCanvas();
        var (w, _) = v.Measure(c);
        v.Render(c);
        var line = Assert.Single(c.Lines);
        double pad = 6, left = pad, right = w - pad, step = (right - left) / (OverlayView.GraphFrames - 1);
        Assert.Equal(right, line[^2], 3);                                  // newest frame on the right edge
        Assert.Equal(left, line[0], 3);                                    // oldest frame on the left edge
        Assert.Equal(right - 49 * step, line[line.Length - 2 - 49 * 2], 3);              // the hitch is one point, one step wide
        Assert.Equal(right - 48 * step, line[line.Length - 2 - 48 * 2], 3);
        v.GraphFrac = 0.5;                                                  // half way to the next frame
        var c2 = new FakeCanvas();
        v.Render(c2);
        Assert.Equal(right - step / 2, c2.Lines[0][^2], 3);
        Assert.Equal(left, c2.Lines[0][0], 3);                              // clipped at the left edge
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
        // 2.0.3 (Amine): DISPLAY, BOUND, LAT side by side, then a gap, then FPS; beta2: no STUTTER row
        var labels = OverlayValuesWith(Item.Latency).BuildRows().Select(r => r.Label).ToList();
        Assert.Equal(labels.IndexOf("DISPLAY") + 1, labels.IndexOf("BOUND"));
        Assert.Equal(labels.IndexOf("BOUND") + 1, labels.IndexOf("LAT"));
        Assert.Equal(labels.IndexOf("LAT") + 2, labels.IndexOf("FPS"));
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
        var buf = new FramesSource.Frame[128];
        int n = f.CopyRecent(buf);
        Assert.Equal(100, n);
        for (int i = 0; i < n; i++) { Assert.Equal(7f, buf[i].Ms, 3); Assert.False(buf[i].Hidden); }
        // beta2.5: fps counts presents like RTSS (avg 7 ms = ~143), not display changes
        var (fps, _) = FramesSource.FpsOver(buf.AsSpan(0, n), buf[n - 1].Ticks, System.Diagnostics.Stopwatch.Frequency);
        Assert.InRange(fps, 140, 146);
        Assert.True(f.OnLine("game.exe,3,0x1,DXGI,Application,1.0,7.0,NA"));   // never reached the screen: kept, hidden
        n = f.CopyRecent(buf);
        Assert.Equal(101, n);
        Assert.True(buf[n - 1].Hidden);
        Assert.Equal(7f, buf[n - 1].PresentMs, 3);
    }

    // beta3: the Metal HUD level
    [Fact]
    public void Metal_level_rows_follow_the_toggles()
    {
        var v = View(2, OverlayLevels.Hidden(true, true, false, false));   // clock tile off
        v.Data = new Sensors { Resolution = "2560x1600", VramGiB = 6.2, RamGiB = 8.8, GpuLoad = 97, GpuTemp = 71, GpuW = 115, CpuLoad = 38, CpuTemp = 68, CpuW = 38 };
        v.Fps = 139.84; v.Low1 = 117.2; v.FrameMs = 7.15;
        v.Graph = new float[] { 7.0f, 8.5f, 6.94f }; v.GraphCount = 3;
        var rows = v.BuildMetal();
        Assert.DoesNotContain(rows, r => r.Label == "Time");                                  // clock tile off
        Assert.Contains(rows, r => r.Label == "Resolution" && r.Find(OverlayView.MCols) == "[2560x1600]");
        Assert.Contains(rows, r => r.Label == "" && r.Find(OverlayView.MCols) == "G-SYNC --");  // no mode without the toggle
        var fps = rows.Single(r => r.Label == "FPS:");
        Assert.Equal("139.84", fps.Find(15));
        Assert.Equal("117.20", fps.Find(23));
        Assert.Equal("133.69", fps.Find(30));                                                 // 1000 / avg of the 3 frames (fewer than 10)
        var frame = rows.Single(r => r.Label == "Frame:");
        Assert.Equal("7.48", frame.Find(23));
        Assert.Equal("8.50", frame.Find(30));
        var gpu = rows.Single(r => r.Label == "GPU:");
        Assert.Equal("97%", gpu.Find(15));
        Assert.Equal("71°C", gpu.Find(23));
        Assert.Equal("115W", gpu.Find(30));
        Assert.Equal(OverlayView.MetalGreen, gpu.Segs.Single(x => x.Col == 23).Color);
        var mem = rows.Single(r => r.Label == "Mem:");
        Assert.Equal("6.2GB", mem.Find(23));
        Assert.Equal("8.8GB", mem.Find(30));
        Assert.DoesNotContain(rows, r => r.Label.Contains("Latency") || r.Label.Contains("Gen") || r.Label.Contains("Fan"));
        Assert.Single(rows, r => r.Graph);

        // uneven pacing (0.2 ms / 14 ms pairs) must not read as thousands of fps
        var zig = new float[200];
        for (int i = 0; i < zig.Length; i++) zig[i] = i % 2 == 0 ? 0.2f : 14f;
        var (zmn, zmx) = OverlayView.MetalRange(zig, zig.Length);
        Assert.InRange(1000 / zmn, 130, 150);
        Assert.Equal(14, zmx, 3);

        v.Data = v.Data with { GpuTemp = 80, CpuTemp = 90 };                                  // Amine: red at GPU 80, CPU 90
        rows = v.BuildMetal();
        Assert.Equal(OverlayView.MetalRed, rows.Single(r => r.Label == "GPU:").Segs.Single(x => x.Col == 23).Color);
        Assert.Equal(OverlayView.MetalRed, rows.Single(r => r.Label == "CPU:").Segs.Single(x => x.Col == 23).Color);

        v.Items = OverlayLevels.Items(2, true, OverlayLevels.Hidden(true, true, true, false));   // clock + latency on
        v.Clock = "13:23"; v.PresentMode = "Hardware: Independent Flip"; v.LatMs = 54; v.LatPc = true;
        rows = v.BuildMetal();
        Assert.Equal("[13:23]", rows[0].Find(OverlayView.MCols));
        Assert.Contains(rows, r => r.Label == "iFlip");
        int lat = rows.FindIndex(r => r.Label == "Latency:");
        Assert.Equal("54ms", rows[lat].Find(OverlayView.MCols));
        Assert.Equal("Frame:", rows[lat - 1].Label);                                          // right after Frame

        // 2.0.1 (Amine): the driver's G-SYNC answer wins; no game = "--" instead of 0.00
        v.Data = v.Data with { VrrOn = true };
        Assert.Contains(v.BuildMetal(), r => r.Find(OverlayView.MCols) == "G-SYNC On");
        v.Data = v.Data with { VrrOn = false };
        Assert.Contains(v.BuildMetal(), r => r.Find(OverlayView.MCols) == "G-SYNC Off");
        v.Fps = 0; v.Low1 = 0; v.FrameMs = 0;
        var idle = v.BuildMetal();
        Assert.Equal("--", idle.Single(r => r.Label == "FPS:").Find(15));
        Assert.Equal("--", idle.Single(r => r.Label == "Frame:").Find(15));
        v.Fps = 139.84; v.Low1 = 117.2; v.FrameMs = 7.15;

        var c = new FakeCanvas();
        var (w, h) = v.Measure(c);
        Assert.True(w > 0 && h > 0);
        v.Render(c);
        Assert.Contains(c.Texts, t => t.S == "iFlip");
    }

    [Fact]
    public void Gsync_is_read_from_the_counted_refresh()
    {
        Assert.True(OverlayView.GsyncState(141, 240, 140, null));     // the panel follows the game
        Assert.False(OverlayView.GsyncState(240, 240, 140, null));    // fixed 240 Hz while the game runs 140
        Assert.True(OverlayView.GsyncState(238, 240, 239, true));     // at the max: can't tell, keep the last
        Assert.Null(OverlayView.GsyncState(null, 240, 140, null));
    }

    [Fact]
    public void Metal_font_ships_in_the_exe()
    {
        var names = typeof(MonoFont).Assembly.GetManifestResourceNames();
        foreach (var f in MonoFont.Files) Assert.Contains("AlienControl.Fonts." + f, names);
    }
}
