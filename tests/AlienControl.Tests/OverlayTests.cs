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
        var (low, max) = OverlayHost.Lows(frames);
        Assert.Equal(20, low, 3);   // the slowest 1 % is the 50 ms frame
        Assert.Equal(50, max, 3);
    }

    [Fact]
    public void Level_one_reads_frames_only()
    {
        Assert.Equal(Need.Frames, OverlayLevels.Needs(1));
        Assert.Equal(Need.None, OverlayLevels.Needs(0));
        Assert.False(OverlayLevels.Needs(2).HasFlag(Need.Acpi));   // level 2 has no temps or fans
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
}
