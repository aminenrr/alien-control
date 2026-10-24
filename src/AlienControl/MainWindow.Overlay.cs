using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AlienControl.Overlay;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// beta1.3-ov: the in-game overlay. Owns the level (Home slider, Overlay tab, F4), the PresentMon frames source and
/// the overlay window, and decides what the window's existing pollers read for it.
///
/// Efficiency rules (Amine): the overlay reads only what its level shows. Level 1 = frames only. Off = PresentMon
/// stopped, overlay window hidden, no extra timer. Sensors come from the window's own pollers (the 2 s sensor poll
/// and the 1 s power timer), which keep running for the overlay while the window is hidden; nothing is read twice.
/// </summary>
public partial class MainWindow
{
    private readonly FramesSource _frames = new();
    private readonly VblankCounter _vblank = new();   // ov.7: real (VRR) refresh rate, only while a level shows RR
    private OverlayHost? _overlay;
    private Need _ovNeed;
    private Sensors _ovSnap = new();
    private readonly CpuCores _cores = new();
    private readonly DispatcherTimer _ovExtra = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _ovPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _ovExtraBusy;
    private int _ovRefreshTick;

    private bool OvNeeds(Need n) => (_ovNeed & n) != 0;

    /// <summary>The level the Overlay tab edits and previews: the current one, or the last one while off.</summary>
    private int OvShownLevel => _settings.OverlayLevel > 0 ? _settings.OverlayLevel : Math.Clamp(_settings.OverlayLastLevel, 1, OverlayLevels.Max);

    /// <summary>ov.3: each level keeps its own position, size, background and move X / Y.</summary>
    private OverlayLook OvLook => _settings.LookFor(OvShownLevel);

    /// <summary>Puts the shown level's look into the Advanced controls (handlers see equal values and save nothing).</summary>
    private void SyncOverlayLookUi()
    {
        var l = OvLook;
        OvSizeSlider.Value = l.Size;
        OvBgSlider.Value = l.Bg;
        OvXSlider.Value = l.X;
        OvYSlider.Value = l.Y;
        PaintOverlayPosition();
    }

    /// <summary>Called once from the constructor, after settings are loaded.</summary>
    private void InitOverlay()
    {
        HomeOverlaySlider.LevelPicked += l => SetOverlayLevel(l, fromKey: false);
        OvTabSlider.LevelPicked += l => SetOverlayLevel(l, fromKey: false);
        _ovExtra.Tick += async (_, _) => await OverlayExtraTickAsync();
        _ovPreviewTimer.Tick += (_, _) => UpdateOverlayPreview();
        OverlayPage.IsVisibleChanged += (_, _) =>
        {
            if (OverlayPage.IsVisible) { UpdateOverlayPreview(); _ovPreviewTimer.Start(); }
            else _ovPreviewTimer.Stop();
        };
        SyncOverlayLookUi();
        OvExtraToggle.IsChecked = _settings.OverlayLatencyFg;
        (_settings.OverlaySmoothGraph ? OvUpdLive : _settings.OverlayUpdateMs switch { 250 => OvUpd250, 1000 => OvUpd1000, _ => OvUpd500 }).IsChecked = true;
        // after the constructor: the window's services are ready, and the app may start hidden in the tray
        Dispatcher.BeginInvoke(new Action(ApplyOverlay), DispatcherPriority.Background);
    }

    /// <summary>The one path for a level change (Home slider, Overlay tab slider, F4).</summary>
    private void SetOverlayLevel(int level, bool fromKey)
    {
        level = Math.Clamp(level, 0, OverlayLevels.Max);
        if (level > 0) _settings.OverlayLastLevel = level;
        if (level == _settings.OverlayLevel && !fromKey) return;
        _settings.OverlayLevel = level;
        _settings.Save();
        ApplyOverlay();
        if (fromKey && _settings.Toasts)
            Toast.Show(true, new Toast.Info(CurrentProfile, "Icon.ChartBar", level == 0 ? "Overlay off" : $"Overlay level {level}",
                level == 0 ? $"F4 brings back level {_settings.OverlayLastLevel}" : LevelName(level), Toast.Right.None, Lit: level > 0,
                Accent: (Application.Current.Resources["AccentBrush"] as SolidColorBrush)?.Color), Motion.Enabled);
    }

    private static string LevelName(int level) => level switch
    {
        1 => "FPS only",
        2 => "One line: FPS, frametime, CPU, GPU, memory, battery",
        3 => "Full HUD with temps, clocks and power",
        4 => "Everything: every core's load and clock, fans, refresh rate",
        _ => "Off",
    };

    private static string LevelItems(int level) => level switch
    {
        1 => "FPS and 1% low.",
        2 => "FPS, frametime and graph, CPU and GPU load and watts, RAM, VRAM, battery, refresh rate.",
        3 => "GPU load, temp, watts, core and memory clock. CPU load, temp, watts, clock. VRAM, RAM, battery, engine, FPS, 1% low, frametime graph.",
        4 => "Level 3 plus every core (P and E) with its threads' load and real clock, fan RPM, refresh rate, 0.1% low and max frametime. With Latency and frame gen on: flip mode (IFLIP or COMPOSED).",
        _ => "Nothing runs: PresentMon is stopped and no sensor is read for the overlay.",
    };

    /// <summary>Brings everything in line with settings: frames source, overlay window, extra timer, pollers, UI.</summary>
    private void ApplyOverlay()
    {
        int level = _settings.OverlayLevel;
        var oldNeed = _ovNeed;
        _ovNeed = OverlayLevels.Needs(level);

        // the latency / frame gen toggle: a hard switch, PresentMon collects none of it unless on (and a level 2+ shows it)
        _frames.SetExtended(OverlayLevels.WantsExtended(level, _settings.OverlayLatencyFg));
        if (level > 0) _frames.Start();   // ov.7: always call, Start cancels a pending debounced stop (F4 off / on within 2 s)
        else _frames.StopSoon();   // ov.4: debounced, F4 spam keeps one PresentMon

        _overlay ??= level > 0 ? new OverlayHost(_frames) : null;
        var look = OvLook;
        _overlay?.Apply(level, look.Position, look.Size / 100.0, look.Bg / 100.0, _settings.OverlayUpdateMs,
            look.X, look.Y, _settings.OverlayLatencyFg, _settings.OverlaySmoothGraph);
        if (OvNeeds(Need.Refresh)) _vblank.Start(); else _vblank.Stop();

        bool extra = OvNeeds(Need.Clocks) || OvNeeds(Need.Cores) || OvNeeds(Need.Battery) || OvNeeds(Need.Refresh);
        if (extra) { if (!_ovExtra.IsEnabled) { _ovExtra.Start(); _ = OverlayExtraTickAsync(); } }
        else _ovExtra.Stop();

        // the NVML session the clocks kept open: give it back when nothing else (the power readout) needs it
        if ((oldNeed & Need.Clocks) != 0 && !OvNeeds(Need.Clocks) && !SensorsShown && !OvNeeds(Need.Power)) NvmlSession.Release();
        if ((oldNeed & (Need.Clocks | Need.Cores)) != 0 && !OvNeeds(Need.Clocks) && !OvNeeds(Need.Cores)) _cores.Release();
        if (level == 0) _ovSnap = new Sensors();

        UpdatePolling();
        if (level > 0 && !SensorsShown) _ = Dispatcher.BeginInvoke(new Action(async () => { await RefreshSensorsAsync(); await RefreshPowerAsync(); }), DispatcherPriority.Background);

        SyncOverlayLookUi();
        HomeOverlaySlider.Level = level;
        OvTabSlider.Level = level;
        HomeOverlayHint.Text = level == 0 ? "F4" : $"LEVEL {level} · F4";
        int shown = level == 0 ? _settings.OverlayLastLevel : level;
        OvLevelInfo.Text = level == 0 ? $"Off (F4 brings back level {_settings.OverlayLastLevel})" : $"Level {level}: {LevelName(level)}";
        OvLevelItems.Text = LevelItems(level);
        OvState.Text = level == 0 ? $"OFF · PREVIEW OF LEVEL {shown}"
            : !FramesSource.Available ? "PRESENTMON MISSING: NO FPS (tools\\PresentMon)"
            : _frames.Error is { } err ? err.ToUpperInvariant()
            : $"ON · LEVEL {level}";
        UpdateOverlayPreview();
    }

    /// <summary>F4 (Macro C): off, or back to the last level. Replaces beta1's "comes in beta 2" hint.</summary>
    private void OnF4Pressed()
    {
        if (!_settings.F4Overlay) return;
        int next = _settings.OverlayLevel > 0 ? 0 : Math.Clamp(_settings.OverlayLastLevel, 1, OverlayLevels.Max);
        Log.Info("F4", $"overlay {_settings.OverlayLevel} -> {next}");
        SetOverlayLevel(next, fromKey: true);
    }

    // ---------- sensors for the overlay (called from the window's pollers) ----------

    /// <summary>The 2 s poll read temps and fans (and usage when asked). Publishes what the level shows.</summary>
    private void OverlayTakeSensors(SensorSnapshot s, double? cpuUse, double? gpuUse, (double Used, double Total)? ram, (double Used, double Total)? vram)
    {
        if (_ovNeed == Need.None) return;
        var cpu = s.Temps.FirstOrDefault(t => t.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));
        var gpu = s.Temps.FirstOrDefault(t => t.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase));
        var cpuFan = s.Fans.FirstOrDefault(f => f.Type == "CPU") ?? s.Fans.ElementAtOrDefault(0);
        var gpuFan = s.Fans.FirstOrDefault(f => f.Type == "GPU") ?? s.Fans.ElementAtOrDefault(1);
        _ovSnap = _ovSnap with
        {
            CpuTemp = cpu?.Celsius ?? _ovSnap.CpuTemp,
            GpuTemp = gpu?.Celsius ?? _ovSnap.GpuTemp,
            CpuFanRpm = cpuFan?.Rpm ?? _ovSnap.CpuFanRpm,
            GpuFanRpm = gpuFan?.Rpm ?? _ovSnap.GpuFanRpm,
            CpuLoad = cpuUse ?? _ovSnap.CpuLoad,
            GpuLoad = gpuUse ?? _ovSnap.GpuLoad,
            GpuOff = _gpu.Kind == GpuModeKind.IGpuOnly,
            RamGiB = ram?.Used ?? _ovSnap.RamGiB,
            VramGiB = vram?.Used ?? _ovSnap.VramGiB,
        };
        _overlay?.Push(_ovSnap);
    }

    /// <summary>The 1 s power timer read CPU / GPU / battery watts.</summary>
    private void OverlayTakePower(PowerMeter.Reading r)
    {
        if (!OvNeeds(Need.Power)) return;
        _ovSnap = _ovSnap with { CpuW = r.CpuW, GpuW = r.GpuW, BatteryW = r.OnBattery ? r.TotalW : null };
        _overlay?.Push(_ovSnap);
    }

    /// <summary>Overlay only reads (clocks, per core load, battery %, refresh rate), 1 s, only for the levels that show them.</summary>
    private async Task OverlayExtraTickAsync()
    {
        if (_ovExtraBusy || _ovNeed == Need.None) return;
        _ovExtraBusy = true;
        try
        {
            var need = _ovNeed;
            bool gpuOn = _gpu.Kind != GpuModeKind.IGpuOnly && !_rtxSwitching;
            bool readRefresh = (need & Need.Refresh) != 0 && (_ovRefreshTick++ % 5 == 0 || _ovSnap.RefreshHz is null);
            var r = await Task.Run(() =>
            {
                // one PDH collect serves both the CPU row clock (level 3) and the per core clocks (level 4)
                int[]? perThreadMhz = (need & (Need.Clocks | Need.Cores)) != 0 ? _cores.Mhz() : null;
                int? mhz = CpuCores.MaxMhz(perThreadMhz);
                (int? core, int? mem) gpu = (need & Need.Clocks) != 0 && gpuOn ? GpuClocks() : (null, null);
                CpuCores.CoreReading[]? cores = (need & Need.Cores) != 0 ? CpuCores.Combine(_cores.Topology, _cores.Loads(), perThreadMhz) : null;
                int? hz = readRefresh ? DisplayRefresh.Read()?.CurrentHz : null;
                return (mhz, gpu, cores, hz);
            });
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            _ovSnap = _ovSnap with
            {
                CpuMhz = r.mhz ?? _ovSnap.CpuMhz,
                GpuCoreMhz = gpuOn ? r.gpu.core ?? _ovSnap.GpuCoreMhz : null,
                GpuMemMhz = gpuOn ? r.gpu.mem ?? _ovSnap.GpuMemMhz : null,
                Cores = r.cores ?? _ovSnap.Cores,
                // ov.7: counted vblanks (what the panel really does under G-Sync), the mode's Hz until / unless that works
                RefreshHz = (need & Need.Refresh) != 0 ? _vblank.Hz ?? r.hz ?? _ovSnap.RefreshHz : _ovSnap.RefreshHz,
                BatteryPct = (need & Need.Battery) != 0 && ps.BatteryLifePercent <= 1 ? (int)Math.Round(ps.BatteryLifePercent * 100) : _ovSnap.BatteryPct,
                OnAc = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online,
                GpuOff = !gpuOn && _gpu.Kind == GpuModeKind.IGpuOnly,
            };
            _overlay?.Push(_ovSnap);
        }
        catch (Exception ex) { Log.Warn("Overlay extra", ex); }
        finally { _ovExtraBusy = false; }
    }

    /// <summary>GPU core and memory clock through the shared NVML session (kept open while the overlay shows clocks;
    /// refused while the RTX is being switched, like every NVML call).</summary>
    private static (int? Core, int? Mem) GpuClocks()
    {
        try
        {
            return NvmlSession.Run<(int?, int?)>(true, dev =>
            {
                int? core = Nvml.nvmlDeviceGetClockInfo(dev, 0, out uint c) == 0 ? (int)c : null;
                int? mem = Nvml.nvmlDeviceGetClockInfo(dev, 2, out uint m) == 0 ? (int)m : null;
                return (core, mem);
            }, (_, _) => (null, null), lockTimeoutMs: 200);
        }
        catch (Exception) { return (null, null); }
    }

    // ---------- Overlay tab: preview and advanced ----------

    private static readonly Sensors Sample = new()
    {
        GpuLoad = 97, GpuTemp = 71, GpuW = 118, GpuCoreMhz = 1755, GpuMemMhz = 7001,
        CpuLoad = 38, CpuTemp = 78, CpuW = 45, CpuMhz = 4100,
        VramGiB = 6.2, RamGiB = 11.4, BatteryPct = 100, OnAc = true,
        CpuFanRpm = 4200, GpuFanRpm = 4350, RefreshHz = 240,
        Cores = new CpuCores.CoreReading[]
        {
            new('P', 1, new double[] { 62, 40 }, 4600), new('P', 2, new double[] { 55, 31 }, 4600), new('P', 3, new double[] { 48, 22 }, 4500),
            new('P', 4, new double[] { 70, 35 }, 4700), new('P', 5, new double[] { 44, 18 }, 4400), new('P', 6, new double[] { 25, 12 }, 4400),
            new('E', 1, new double[] { 30 }, 3500), new('E', 2, new double[] { 9 }, 3500), new('E', 3, new double[] { 21 }, 3400), new('E', 4, new double[] { 14 }, 3400),
            new('E', 5, new double[] { 11 }, 3300), new('E', 6, new double[] { 8 }, 3300), new('E', 7, new double[] { 16 }, 3300), new('E', 8, new double[] { 6 }, 3300),
        },
    };

    private readonly float[] _previewGraph = new float[160];
    private int _previewPhase;

    private void UpdateOverlayPreview()
    {
        if (OvPreview is null || !OverlayPage.IsVisible) return;
        int level = OvShownLevel;
        var look = OvLook;
        _previewPhase++;
        var rnd = new Random(_previewPhase);
        for (int i = 0; i < _previewGraph.Length; i++)
        {
            int k = i + _previewPhase * 4;
            _previewGraph[i] = (float)(7.0 + 0.6 * Math.Sin(k * 0.21) + ((k * 37) % 97 == 0 ? 5 : 0) + new Random(k).NextDouble() * 0.5);
        }
        OvPreview.Level = level;
        OvPreview.Items = OverlayLevels.Items(level, _settings.OverlayLatencyFg);
        OvPreview.Scale = look.Size / 100.0;
        OvPreview.BgAlpha = look.Bg / 100.0;
        OvPreview.Data = Sample;
        OvPreview.Fps = 142 + rnd.Next(-2, 3);
        OvPreview.FrameMs = 1000.0 / OvPreview.Fps;
        // sample: frame gen doubling a 71 fps game, Reflex PC latency
        OvPreview.GenFps = _settings.OverlayLatencyFg ? 71 : 0;
        if (_settings.OverlayLatencyFg) { OvPreview.Fps = 71; OvPreview.FrameMs = 1000.0 / 142; }
        OvPreview.LatMs = 28 + rnd.Next(-2, 3);
        OvPreview.LatPc = true;
        OvPreview.Engine = "DX12";
        OvPreview.Graph = _previewGraph;
        OvPreview.GraphCount = _previewGraph.Length;
        OvPreview.Low1 = 98;
        OvPreview.Low01 = 71;
        OvPreview.PresentMode = "Hardware: Independent Flip";
        OvPreview.MaxMs = 12.4;
        OvPreview.InvalidateMeasure();
        OvPreview.InvalidateVisual();
        int pos = look.Position;
        OvPreviewBox.HorizontalAlignment = (pos % 3) switch { 0 => HorizontalAlignment.Left, 1 => HorizontalAlignment.Center, _ => HorizontalAlignment.Right };
        OvPreviewBox.VerticalAlignment = pos < 3 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        // the nudge on the preview: same % of the preview area, kept inside it
        if (OvPreviewBox.Parent is FrameworkElement area && area.ActualWidth > 0)
        {
            double w = OvPreviewBox.ActualWidth, h = OvPreviewBox.ActualHeight;
            double baseX = (pos % 3) switch { 0 => 0, 1 => (area.ActualWidth - w) / 2, _ => area.ActualWidth - w };
            double baseY = pos < 3 ? 0 : area.ActualHeight - h;
            double x = Math.Clamp(baseX + area.ActualWidth * look.X / 100.0, 0, Math.Max(0, area.ActualWidth - w));
            double y = Math.Clamp(baseY + area.ActualHeight * look.Y / 100.0, 0, Math.Max(0, area.ActualHeight - h));
            OvPreviewBox.RenderTransform = new TranslateTransform(x - baseX, y - baseY);
        }
    }

    private void PaintOverlayPosition()
    {
        foreach (Border b in OvPosGrid.Children)
        {
            bool on = int.TryParse(b.Tag?.ToString(), out int i) && i == OvLook.Position;
            if (on) b.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            else b.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x21, 0x27));
        }
        OvPosText.Text = (OvLook.Position switch
        {
            0 => "Top left", 1 => "Top centre", 2 => "Top right",
            3 => "Bottom left", 4 => "Bottom centre", _ => "Bottom right",
        }) + $" · level {OvShownLevel}";
    }

    private void OvPos_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: var t } || !int.TryParse(t?.ToString(), out int pos)) return;
        OvLook.Position = Math.Clamp(pos, 0, 5);
        _settings.Save();
        PaintOverlayPosition();
        ApplyOverlay();
    }

    private void OvSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OvSizeValue is null) return;   // during InitializeComponent
        OvSizeValue.Text = $"{(int)e.NewValue}%";
        if ((int)e.NewValue == OvLook.Size) return;
        OvLook.Size = (int)e.NewValue;
        _settings.Save();
        ApplyOverlay();
    }

    private void OvBg_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OvBgValue is null) return;
        OvBgValue.Text = $"{(int)e.NewValue}%";
        if ((int)e.NewValue == OvLook.Bg) return;
        OvLook.Bg = (int)e.NewValue;
        _settings.Save();
        ApplyOverlay();
    }

    private void OvX_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OvXValue is null) return;
        OvXValue.Text = OffsetText((int)e.NewValue);
        if ((int)e.NewValue == OvLook.X) return;
        OvLook.X = (int)e.NewValue;
        _settings.Save();
        ApplyOverlay();
    }

    private void OvY_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OvYValue is null) return;
        OvYValue.Text = OffsetText((int)e.NewValue);
        if ((int)e.NewValue == OvLook.Y) return;
        OvLook.Y = (int)e.NewValue;
        _settings.Save();
        ApplyOverlay();
    }

    private void OvOffset_Reset(object sender, MouseButtonEventArgs e)
    {
        if (sender == OvXSlider) OvXSlider.Value = 0; else OvYSlider.Value = 0;
        e.Handled = true;
    }

    private void OvExtra_Click(object sender, RoutedEventArgs e)
    {
        _settings.OverlayLatencyFg = OvExtraToggle.IsChecked == true;
        _settings.Save();
        Log.Info("Overlay", $"latency and frame gen {(_settings.OverlayLatencyFg ? "on" : "off")}");
        ApplyOverlay();
    }

    private static string OffsetText(int v) => v == 0 ? "0" : v > 0 ? $"+{v}%" : $"{v}%";

    private void OvUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: var t }) return;
        // ov.7: LIVE = numbers every 250 ms and the frametime graph redrawn ~30 times a second (smooth like RTSS)
        bool live = t?.ToString() == "live";
        if (!live && !int.TryParse(t?.ToString(), out _)) return;
        _settings.OverlaySmoothGraph = live;
        _settings.OverlayUpdateMs = live ? 250 : int.Parse(t!.ToString()!);
        _settings.Save();
        ApplyOverlay();
    }

    private void OvReset_Click(object sender, MouseButtonEventArgs e)
    {
        // ov.3: resets the shown level's look only (the other levels keep theirs); update speed is shared
        int level = OvShownLevel;
        var d = new OverlayLook();
        var l = OvLook;
        l.Position = d.Position; l.Size = d.Size; l.Bg = d.Bg; l.X = d.X; l.Y = d.Y;
        _settings.Save();
        SyncOverlayLookUi();
        ApplyOverlay();
        Status($"Level {level} look reset to SteamOS.");
    }

    /// <summary>Every exit path (Quit, Reset, crash cleanup, Windows ending the session): stop PresentMon and close the
    /// overlay window. The job object kills PresentMon even when this never runs.</summary>
    private void StopOverlayOnExit()
    {
        try { _ovExtra.Stop(); _ovPreviewTimer.Stop(); } catch { /* wrong thread on the crash path */ }
        try { _vblank.Stop(); } catch { /* ignore */ }
        try { _frames.StopAndWait(); } catch (Exception e) { Log.Warn("Overlay exit frames", e); }
        try { _overlay?.Dispose(); } catch (Exception e) { Log.Warn("Overlay exit window", e); }
    }
}
