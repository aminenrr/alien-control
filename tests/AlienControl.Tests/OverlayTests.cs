using System.Linq;
using System;
using System.Diagnostics;
using AlienControl.Overlay;
using AlienControl.Services;
using Xunit;

namespace AlienControl.Tests;

/// <summary>beta1.3-ov: PresentMon parsing, fps math and the level presets.</summary>
public class OverlayTests
{
    private const string Header = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,CPUStartTime,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI";

    [Fact]
    public void Header_finds_the_columns()
    {
        var f = new FramesSource();
        Assert.True(f.ParseHeader(Header));
        Assert.False(f.ParseHeader("Application,SwapChainAddress"));
    }

    [Fact]
    public void Only_foreground_frames_are_kept()
    {
        var f = new FramesSource();
        f.ParseHeader(Header);
        f.SetForegroundForTest(1234);
        Assert.True(f.OnLine("game.exe,1234,0x1,DXGI,0,0,1.0,0,6.944,0,0.1"));
        Assert.False(f.OnLine("chrome.exe,999,0x2,DXGI,1,0,1.0,0,16.6,0,0.1"));
        Assert.False(f.OnLine("game.exe,1234,0x1,DXGI,0,0,1.0,0,NA,0,0.1"));
        Assert.Equal("game.exe", f.TargetApp);
        Assert.Equal("DXGI", f.Runtime);
        var buf = new FramesSource.Frame[8];
        Assert.Equal(1, f.CopyRecent(buf));
        Assert.Equal(6.944f, buf[0].Ms, 3);
    }

    [Fact]
    public void Fps_is_frames_over_the_window()
    {
        long now = Stopwatch.GetTimestamp(), sec = Stopwatch.Frequency;
        var frames = new FramesSource.Frame[200];
        for (int i = 0; i < frames.Length; i++) frames[i] = new(now, i * 0.01, 10f);   // 100 fps for 2 s, all arrived in one burst
        var (fps, avg) = FramesSource.FpsOver(frames, now, sec / 2);
        Assert.InRange(fps, 99.9, 100.1);
        Assert.Equal(10, avg, 3);
        Assert.Equal((0, 0), FramesSource.FpsOver(ReadOnlySpan<FramesSource.Frame>.Empty, now, sec));
        Assert.Equal(0, FramesSource.FpsOver(frames, now + sec * 2, sec / 2).Fps);   // stale: nothing new for 2 s
    }

    [Fact]
    public void One_percent_low()
    {
        var frames = new FramesSource.Frame[100];
        for (int i = 0; i < 100; i++) frames[i] = new(i, i * 0.01, i == 50 ? 50f : 10f);
        var (low, low01, max) = OverlayHost.Lows(frames);
        Assert.Equal(20, low, 3);   // the slowest 1 % is the 50 ms frame
        Assert.Equal(20, low01, 3); // under 1000 frames the 0.1 % is the single slowest frame
        Assert.Equal(50, max, 3);
        var many = new FramesSource.Frame[2000];
        for (int i = 0; i < 2000; i++) many[i] = new(i, i * 0.01, i < 2 ? 100f : i < 20 ? 20f : 10f);
        var (l1, l01, _) = OverlayHost.Lows(many);
        Assert.Equal(10, l01, 3);   // slowest 2 frames: 100 ms
        Assert.Equal(1000.0 / ((2 * 100 + 18 * 20) / 20.0), l1, 3);
    }

    [Fact]
    public void Level_one_reads_frames_only()
    {
        Assert.Equal(Need.Frames, OverlayLevels.Needs(1));
        Assert.Equal(Need.None, OverlayLevels.Needs(0));
        Assert.True(OverlayLevels.Needs(2).HasFlag(Need.Acpi));    // ov.15: level 2 shows CPU and GPU temps (Amine)
        Assert.True(OverlayLevels.Needs(3).HasFlag(Need.Clocks));
        Assert.True(OverlayLevels.Needs(4).HasFlag(Need.Cores));
        Assert.False(OverlayLevels.Needs(3).HasFlag(Need.Cores));
        Assert.True(OverlayLevels.Horizontal(2));
    }

    [Fact]
    public void Overlay_settings_defaults_match_steamos()
    {
        var s = new Settings();
        Assert.Equal(0, s.OverlayLevel);
        Assert.Equal(0, s.OverlayPosition);
        Assert.Equal(50, s.OverlayBg);
        Assert.Equal(500, s.OverlayUpdateMs);
        Assert.Equal(100, s.OverlaySize);
    }

    [Fact]
    public void Offsets_and_latency_toggle_default_off_and_clamp()
    {
        var s = new Settings();
        Assert.Equal(0, s.OverlayOffsetX);
        Assert.Equal(0, s.OverlayOffsetY);
        Assert.False(s.OverlayLatencyFg);
        s.OverlayOffsetX = 400; s.OverlayOffsetY = -400;
        s.Clamp();
        Assert.Equal(0, s.OverlayOffsetX);   // out of range falls back to 0
        Assert.Equal(0, s.OverlayOffsetY);
        s.OverlayOffsetX = -20; s.Clamp();
        Assert.Equal(-20, s.OverlayOffsetX);
    }

    [Fact]
    public void Lean_mode_tracks_no_display_and_extended_adds_it()
    {
        var off = FramesSource.Args(false);
        Assert.Contains("--no_track_display", off);
        Assert.DoesNotContain("--track_frame_type", off);   // beta2.2: back to ov.14's lean set (overflow)
        Assert.DoesNotContain("--track_frame_type", FramesSource.Args(false, 1));
        Assert.DoesNotContain("--track_pc_latency", off);
        var on = FramesSource.Args(true);
        Assert.DoesNotContain("--no_track_display", on);
        Assert.Contains("--track_frame_type", on);
        Assert.Contains("--track_pc_latency", on);
        Assert.DoesNotContain("--track_frame_type", FramesSource.Args(true, 1));
        // ov.4: never stop an ETW session unless PresentMon reported a leftover one of ours
        Assert.DoesNotContain("--stop_existing_session", off);
        Assert.DoesNotContain("--stop_existing_session", on);
        Assert.DoesNotContain("--terminate_existing_session", on);
        Assert.Contains("--stop_existing_session", FramesSource.Args(false, 0, stopExisting: true));
        Assert.Equal(OverlayLevels.Items(1), OverlayLevels.Items(1, true));   // level 1 stays FPS only
        Assert.True(OverlayLevels.Items(2, true).HasFlag(Item.FrameGen));
        Assert.False(OverlayLevels.Items(3, false).HasFlag(Item.Latency));
        Assert.False(OverlayLevels.WantsExtended(1, true));
        Assert.True(OverlayLevels.Items(4, true).HasFlag(Item.PresentMode));    // ov.6: level 4 + toggle only
        Assert.False(OverlayLevels.Items(4, false).HasFlag(Item.PresentMode));
        Assert.False(OverlayLevels.Items(3, true).HasFlag(Item.PresentMode));
        Assert.Equal("IFLIP", OverlayPanel.ModeLabel("Hardware: Independent Flip"));
        Assert.Equal("COMPOSED", OverlayPanel.ModeLabel("Composed: Flip"));
        Assert.False(OverlayLevels.WantsExtended(4, false));
        Assert.True(OverlayLevels.WantsExtended(2, true));
    }

    [Fact]
    public void Frame_gen_splits_real_and_generated_and_reads_latency()
    {
        const string h = "Application,ProcessID,SwapChainAddress,PresentRuntime,FrameType,CPUStartTime,MsBetweenPresents,DisplayLatency,MsPCLatency";
        var f = new FramesSource();
        Assert.True(f.ParseHeader(h));
        f.SetForegroundForTest(7);
        for (int i = 0; i < 100; i++)
        {
            bool gen = i % 2 == 1;
            f.OnLine($"game.exe,7,0x1,DXGI,{(gen ? "AMD AFMF" : "Application")},{(i * 0.005).ToString(System.Globalization.CultureInfo.InvariantCulture)},5.0,12.0,{(gen ? "NA" : "30.0")}");
        }
        var buf = new FramesSource.Frame[200];
        int n = f.CopyRecent(buf);
        var st = FramesSource.StatsOver(buf.AsSpan(0, n), buf[n - 1].Ticks, Stopwatch.Frequency / 4);
        Assert.InRange(st.Fps, 190, 210);     // beta2.2: every present counts, like RTSS (200/s, half generated)
        Assert.InRange(st.GenFps, 95, 105);
        Assert.True(st.PcLat);
        Assert.Equal(30, st.LatMs, 3);
    }

    [Fact]
    public void No_generated_frames_means_real_is_all()
    {
        long now = Stopwatch.GetTimestamp();
        var frames = new FramesSource.Frame[100];
        for (int i = 0; i < 100; i++) frames[i] = new(now, i * 0.01, 10f);
        var st = FramesSource.StatsOver(frames, now, Stopwatch.Frequency / 2);
        Assert.InRange(st.Fps, 99.9, 100.1);
        Assert.Equal(0, st.GenFps);
        Assert.True(double.IsNaN(st.LatMs));
        Assert.True(FramesSource.IsGenerated("Intel XeFG"));
        Assert.False(FramesSource.IsGenerated("Application"));
        Assert.False(FramesSource.IsGenerated("Repeated"));
    }

    [Fact]
    public void Hybrid_topology_labels_p_then_e_cores()
    {
        var raw = new System.Collections.Generic.List<(byte, int[])>
        {
            (1, new[] { 0, 1 }), (1, new[] { 2, 3 }), (0, new[] { 4 }), (0, new[] { 5 }),
        };
        var cores = CpuCores.Label(raw, 6);
        Assert.Equal(new[] { "P1", "P2", "E1", "E2" }, cores.Select(c => $"{c.Kind}{c.Index}").ToArray());
        var flat = CpuCores.Label(new() { (0, new[] { 0, 1 }), (0, new[] { 2, 3 }) }, 4);
        Assert.All(flat, c => Assert.Equal('C', c.Kind));
        Assert.Equal(4, CpuCores.Label(new(), 4).Count);   // no topology: one row per thread
    }

    [Fact]
    public void Core_clock_is_its_fastest_thread()
    {
        var cores = CpuCores.Label(new() { (1, new[] { 0, 1 }), (0, new[] { 2 }) }, 3);
        var r = CpuCores.Combine(cores, new[] { 10.0, 50.0, 80.0 }, new[] { 4100, 4300, 3200 });
        Assert.Equal(4300, r[0].Mhz);
        Assert.Equal(new[] { 10.0, 50.0 }, r[0].Loads);
        Assert.Equal(3200, r[1].Mhz);
        Assert.Null(CpuCores.Combine(cores, null, null)[0].Mhz);
        Assert.Equal(4300, CpuCores.MaxMhz(new[] { 4100, 4300, 3200 }));
    }

    [Fact]
    public void Each_level_keeps_its_own_look()
    {
        var s = new Settings { OverlayPosition = 2, OverlaySize = 120 };   // an ov.2 file: one look for all levels
        Assert.Equal(2, s.LookFor(1).Position);                          // seeded from the old single values
        Assert.Equal(120, s.LookFor(4).Size);
        s.LookFor(3).Position = 5; s.LookFor(3).X = -10; s.LookFor(3).Bg = 80;
        Assert.Equal(2, s.LookFor(2).Position);                          // level 2 untouched
        Assert.Equal(0, s.LookFor(2).X);
        Assert.Equal(5, s.LookFor(3).Position);
        var back = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(s))!;
        Assert.Equal(-10, back.LookFor(3).X);
        Assert.Equal(80, back.LookFor(3).Bg);
        back.LookFor(1).Size = 999; back.Clamp();
        Assert.Equal(100, back.LookFor(1).Size);                         // out of range back to SteamOS
    }

    [Fact]
    public void Show_on_overlay_hides_refresh_and_battery_everywhere()
    {
        // ov.9: level 3 has refresh too; the toggles drop it (and battery) from every level and from what gets read
        foreach (int lv in new[] { 2, 3, 4 }) Assert.True(OverlayLevels.Items(lv).HasFlag(Item.Refresh));
        var hide = OverlayLevels.Hidden(showRefresh: false, showBattery: false);
        for (int lv = 1; lv <= 4; lv++)
        {
            var it = OverlayLevels.Items(lv, true, hide);
            Assert.False(it.HasFlag(Item.Refresh));
            Assert.False(it.HasFlag(Item.Battery));
            var n = OverlayLevels.Needs(lv, hide);
            Assert.False(n.HasFlag(Need.Refresh));
            Assert.False(n.HasFlag(Need.Battery));
        }
        Assert.Equal(Item.None, OverlayLevels.Hidden(true, true, true, true));
        Assert.True(OverlayLevels.Items(3, false, Item.None).HasFlag(Item.Battery));
    }

    [Fact]
    public void Swapchain_filter_keeps_the_busiest_one()
    {
        var f = new FramesSource();
        f.ParseHeader(Header);
        f.SetForegroundForTest(42);
        string L(string swap, int i) => $"game.exe,42,{swap},DXGI,0,0,{(i * 0.004).ToString(System.Globalization.CultureInfo.InvariantCulture)},0,4.0,0,0.1";
        // first present picks its swapchain until the counts say otherwise
        Assert.True(f.OnLine(L("0x00000226A1B2C3D0", 0)));
        Assert.False(f.OnLine(L("0x00000226FFFF0000", 1)));
        Assert.True(f.OnLine(L("0x00000226A1B2C3D0", 2)));
        Assert.Equal("game.exe", f.TargetApp);
    }

    [Fact]
    public void Parser_handles_spaces_and_short_lines()
    {
        var f = new FramesSource();
        f.ParseHeader(Header);
        f.SetForegroundForTest(5);
        Assert.True(f.OnLine("game.exe, 5 ,0x1,DXGI,0,0,1.0,0, 8.5 ,0,0.1"));
        Assert.False(f.OnLine("game.exe,5"));
        Assert.False(f.OnLine(""));
    }

    [Fact]
    public void Gsync_preset_is_three_under_refresh()
    {
        Assert.Equal(237, NvDrs.GsyncCap(240));
        Assert.Equal(141, NvDrs.GsyncCap(144));
        Assert.Equal(NvDrs.MinFps, NvDrs.GsyncCap(10));
    }

    [Fact]
    public void Look_steppers_clamp_and_snap()
    {
        Assert.Equal(105, OverlayLook.StepSize(100, 1, false));
        Assert.Equal(125, OverlayLook.StepSize(100, 1, true));
        Assert.Equal(150, OverlayLook.StepSize(145, 1, true));
        Assert.Equal(50, OverlayLook.StepSize(50, -1, false));
        Assert.Equal(105, OverlayLook.StepSize(102, 1, false));
        Assert.Equal(4, OverlayLook.StepOffset(3, 1, false));
        Assert.Equal(-50, OverlayLook.StepOffset(-45, -1, true));
    }

    [Fact]
    public void Playhead_plays_a_burst_back_one_frame_at_a_time()
    {
        long f = Stopwatch.Frequency;
        var ph = new FramesSource.Playhead();
        // 60 fps; frames 0..59 arrive on time, then 60..119 all land in one burst 1 s late
        var fr = new FramesSource.Frame[120];
        for (int i = 0; i < 120; i++)
        {
            double t = 10 + i / 60.0;
            long arrive = (long)((i < 60 ? t : 10 + 119 / 60.0) * f);
            fr[i] = new FramesSource.Frame(arrive, t, 16.7f);
        }
        long now = fr[119].Ticks;
        int a = ph.Visible(fr, now);
        int b = ph.Visible(fr, now + f / 10);
        Assert.True(a < 120);          // the burst isn't dumped at once
        Assert.True(b > a && b - a <= 8); // about 6 frames per 100 ms at 60 fps
    }

    [Fact]
    public void Playhead_never_moves_back_and_waits_at_the_newest_frame()
    {
        long f = Stopwatch.Frequency;
        var ph = new FramesSource.Playhead();
        var fr = new System.Collections.Generic.List<FramesSource.Frame>();
        double last = double.MinValue;
        // 100 fps arriving on time for 1 s, then nothing for 0.5 s, then the gap's frames all in one burst
        for (int step = 0; step < 300; step++)
        {
            double now = 10 + step / 200.0;   // 200 Hz overlay
            for (int i = fr.Count; i < 300; i++)
            {
                double t = 10 + i / 100.0;
                double arrive = t < 11 ? t : (t < 11.5 ? 11.5 : t);
                if (arrive > now) break;
                fr.Add(new FramesSource.Frame((long)(arrive * f), t, 10f));
            }
            if (fr.Count == 0) continue;
            ph.Visible(fr.ToArray(), (long)(now * f));
            Assert.True(ph.Now >= last - 1e-9);                        // never back
            Assert.True(ph.Now <= fr[^1].T + 1e-9 || ph.Now == last);  // never past the newest frame
            last = ph.Now;
        }
    }

    [Fact]
    public void No_frame_type_never_guesses_generated()
    {
        // beta2.2 (Amine: show fps like RTSS): FG pairs without a frame type are plain frames
        const string h = "Application,ProcessID,SwapChainAddress,PresentRuntime,CPUStartTime,MsBetweenPresents";
        var f = new FramesSource();
        Assert.True(f.ParseHeader(h));
        f.SetForegroundForTest(7);
        double t = 0;
        for (int i = 0; i < 400; i++)
        {
            float ms = i % 2 == 1 ? 0.2f : 13.8f;
            t += ms / 1000.0;
            f.OnLine($"game.exe,7,0x1,DXGI,{t.ToString(System.Globalization.CultureInfo.InvariantCulture)},{ms.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        var buf = new FramesSource.Frame[512];
        int n = f.CopyRecent(buf);
        Assert.Equal(400, n);
        for (int i = 0; i < n; i++) Assert.False(buf[i].Gen);
        var (fps, _) = FramesSource.FpsOver(buf.AsSpan(0, n), buf[n - 1].Ticks, Stopwatch.Frequency / 4);
        Assert.InRange(fps, 138, 147);   // 2 presents per 14 ms
    }
}
