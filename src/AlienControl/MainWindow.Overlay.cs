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
    private Item OvHidden => OverlayLevels.Hidden(_settings.OverlayShowRefresh, _settings.OverlayShowBattery,
        _settings.OverlayShowClock, _settings.OverlayShowBound);   // ov.9, ov.15 clock and bound

    /// <summary>The level the Overlay tab edits and previews: the current one, or the last one while off.</summary>
    private int OvShownLevel => _settings.OverlayLevel > 0 ? _settings.OverlayLevel : Math.Clamp(_settings.OverlayLastLevel, 1, OverlayLevels.Max);

    /// <summary>ov.3: each level keeps its own position, size, background and move X / Y.</summary>
    private OverlayLook OvLook => _settings.LookFor(OvShownLevel);

    /// <summary>Puts the shown level's look into the Advanced controls (handlers see equal values and save nothing).</summary>
    private void SyncOverlayLookUi()
    {
        var l = OvLook;
        OvBgSlider.Value = l.Bg;
        PaintOverlaySteppers();
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
            if (OverlayPage.IsVisible) { UpdateOverlayPreview(); _ovPreviewTimer.Start(); _ = RefreshFpsCapAsync(); }
            else _ovPreviewTimer.Stop();
        };
        SyncOverlayLookUi();
        OvExtraToggle.IsChecked = _settings.OverlayLatencyFg;
        OvShowRefresh.IsChecked = _settings.OverlayShowRefresh;
        OvShowBattery.IsChecked = _settings.OverlayShowBattery;
        OvShowClock.IsChecked = _settings.OverlayShowClock;
        OvShowBound.IsChecked = _settings.OverlayShowBound;
        PaintShowSub();
        InitFpsCap();
        _frames.TargetChanged += (app, pid) => Dispatcher.BeginInvoke(new Action(() => { NoteCapGame(app, pid); _ = RefreshBoundLimitAsync(app); }));
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
            Toast.Show(new Toast.Info(CurrentProfile, "Icon.ChartBar", level == 0 ? "Overlay off" : $"Overlay level {level}",
                level == 0 ? $"F4 brings back level {_settings.OverlayLastLevel}" : LevelName(level), Toast.Right.None, Lit: level > 0,
                Accent: AccentColor), Motion.Enabled);
    }

    private static string LevelName(int level) => level switch
    {
        1 => "One line: FPS, frametime, CPU, GPU, memory, battery",
        2 => "Metal HUD: macOS style, display, FPS, frame interval, GPU, CPU",
        3 => "Full HUD with temps, clocks and power",
        4 => "Everything: every core's load and clock, fans, refresh rate",
        _ => "Off",
    };

    private static string LevelItems(int level) => level switch
    {
        1 => "FPS, frametime and graph, CPU and GPU load, temp and watts, RAM, VRAM, battery, refresh rate.",
        2 => "Resolution, VRAM, RAM, G-SYNC, FPS, 1% low, frame interval and graph, GPU and CPU load, temp and watts. Clock with its tile, flip mode and latency with Latency on.",
        3 => "GPU load, temp, watts, core and memory clock. CPU load, temp, watts, clock. VRAM, RAM, battery, refresh rate, engine, FPS, stutters, 1% low, frametime graph. Clock and bound with their tiles.",
        4 => "Level 3 plus every core (P and E) with its threads' load and real clock, fan RPM, 0.1% low and max frametime. With Latency and frame gen on: flip mode (IFLIP or COMPOSED).",
        _ => "Nothing runs: PresentMon is stopped and no sensor is read for the overlay.",
    };

    /// <summary>Brings everything in line with settings: frames source, overlay window, extra timer, pollers, UI.</summary>
    private void ApplyOverlay()
    {
        int level = _settings.OverlayLevel;
        var oldNeed = _ovNeed;
        _ovNeed = OverlayLevels.Needs(level, OvHidden);

        // the latency / frame gen toggle: a hard switch, PresentMon collects none of it unless on (and a level 2+ shows it)
        _frames.SetExtended(OverlayLevels.WantsExtended(level, _settings.OverlayLatencyFg));
        // ov.15: GPU tracking only while the BOUND row is shown (its tile on, level 3 or 4)
        _frames.SetTrackGpu(OvNeeds(Need.GpuBusy));
        if (OvNeeds(Need.GpuBusy) && (oldNeed & Need.GpuBusy) == 0) _ = RefreshBoundLimitAsync(_frames.TargetApp);
        if (level > 0) _frames.Start();   // ov.7: always call, Start cancels a pending debounced stop (F4 off / on within 2 s)
        else _frames.StopSoon();   // ov.4: debounced, F4 spam keeps one PresentMon

        if (_overlay is null && level > 0)
        {
            _overlay = new OverlayHost(_frames);
            // ov.11: 30 s after off the overlay window and its thread are gone; give that memory back (Amine saw
            // 50 -> 75 MB stay after using the overlay)
            _overlay.TornDown += () => Task.Run(TrimMemory);
        }
        var look = OvLook;
        _overlay?.Apply(level, look.Position, look.Size / 100.0, look.Bg / 100.0, _settings.OverlayUpdateMs,
            look.X, look.Y, _settings.OverlayLatencyFg, _settings.OverlaySmoothGraph, OvHidden, _settings.OverlayRenderer);
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
        HomeOverlayHint.Text = level == 0 ? "F4" : $"LEVEL {level} · F4";   // Shift + F4 cycles (the Overlay tab says so)
        int shown = level == 0 ? _settings.OverlayLastLevel : level;
        OvLevelInfo.Text = level == 0 ? $"Off (F4 brings back level {_settings.OverlayLastLevel})" : $"Level {level}: {LevelName(level)}";
        OvLevelItems.Text = LevelItems(level);
        OvTabSlider.ToolTip = (level == 0 ? OvLevelInfo.Text : LevelName(level)) + "\n" + LevelItems(level);
        OvState.Text = level == 0 ? $"OFF · PREVIEW OF LEVEL {shown}"
            : !FramesSource.Available ? "PRESENTMON MISSING: NO FPS (tools\\PresentMon)"
            : _frames.Error is { } err ? err.ToUpperInvariant()
            : $"ON · LEVEL {level}";
        UpdateOverlayPreview();
    }

    /// <summary>F4 (Macro C): off, or back to the last level. Replaces beta1's "comes in beta 2" hint.
    /// ov.15 (Amine): Shift + F4 cycles levels 1 to 4 (from off it starts at 1). Not Alt: Alt + F4 closes the game.
    /// shift / f4Down are read with GetAsyncKeyState when the macro report arrives; f4Down is only logged, to check the
    /// key sends the Alienware macro report alone and no real F4 to the game.</summary>
    private void OnF4Pressed(bool shift = false, bool f4Down = false)
    {
        if (!_settings.F4Overlay) return;
        int level = _settings.OverlayLevel;
        int next = OverlayLevels.OnKey(level, _settings.OverlayLastLevel, shift);
        Log.Info("F4", $"overlay {level} -> {next}{(shift ? " (Shift)" : "")}{(f4Down ? ", VK_F4 down too" : "")}");
        SetOverlayLevel(next, fromKey: true);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    /// <summary>ov.15: is Shift held, and is a real F4 down too (logged only), when the F4 macro report arrives.</summary>
    private static (bool Shift, bool F4) OverlayKeys()
    {
        try { return ((GetAsyncKeyState(0x10) & 0x8000) != 0, (GetAsyncKeyState(0x73) & 0x8000) != 0); }
        catch (Exception) { return (false, false); }
    }

    /// <summary>ov.15 BOUND: the game's NVIDIA cap and driver V-Sync, and the mode's Hz, so a capped game reads LIMITED
    /// instead of CPU. Once per game (and when the row turns on), only while the row is shown.</summary>
    private async Task RefreshBoundLimitAsync(string exe)
    {
        if (!OvNeeds(Need.GpuBusy)) return;
        try
        {
            var r = await Task.Run(() =>
            {
                int cap = 0; bool vsync = false;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    var (st, _, err) = NvDrs.ReadFull(exe);
                    if (err is null) { cap = st.Fps; vsync = st.Vsync; }
                }
                return (cap, vsync, hz: DisplayRefresh.Read()?.CurrentHz);
            });
            _ovSnap = _ovSnap with { CapFps = r.cap, DriverVsync = r.vsync, ModeHz = r.hz };
            _overlay?.Push(_ovSnap);
            Log.Info("Overlay", $"bound limits for {(exe.Length > 0 ? exe : "no game")}: cap {r.cap}, driver vsync {(r.vsync ? "on" : "off")}, {r.hz?.ToString() ?? "?"} Hz");
        }
        catch (Exception ex) { Log.Warn("Overlay bound limits", ex); }
    }

    // ---------- sensors for the overlay (called from the window's pollers) ----------

    /// <summary>The 2 s poll read temps and fans (and usage when asked). Publishes what the level shows.</summary>
    private void OverlayTakeSensors(SensorSnapshot s, TempReading? gpuTemp, double? cpuUse, double? gpuUse, (double Used, double Total)? ram, (double Used, double Total)? vram)
    {
        if (_ovNeed == Need.None) return;
        var cpu = s.Temps.FirstOrDefault(t => t.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));
        var gpu = gpuTemp;   // beta2 merge: the Home GPU reading (beta1.4 IGCL / CPU package in iGPU only)
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
                var disp = readRefresh ? DisplayRefresh.Read() : null;
                int? hz = disp?.CurrentHz;
                if (hz is int modeHz) _vblank.MaxHz = modeHz;   // ov.7.1: caps and de-duplicates the vblank count
                string? res = disp is { Width: > 0, Height: > 0 } ? $"{disp.Width}x{disp.Height}" : null;   // beta3 Metal level
                return (mhz, gpu, cores, hz, res);
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
                ModeHz = r.hz ?? _ovSnap.ModeHz,            // beta3: the G-SYNC row compares the counted Hz with this
                Resolution = r.res ?? _ovSnap.Resolution,
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
        CpuFanRpm = 4200, GpuFanRpm = 4350, RefreshHz = 141, ModeHz = 240, Resolution = "2560x1600",
        Cores = new CpuCores.CoreReading[]
        {
            new('P', 1, new double[] { 62, 40 }, 4600), new('P', 2, new double[] { 55, 31 }, 4600), new('P', 3, new double[] { 48, 22 }, 4500),
            new('P', 4, new double[] { 70, 35 }, 4700), new('P', 5, new double[] { 44, 18 }, 4400), new('P', 6, new double[] { 25, 12 }, 4400),
            new('E', 1, new double[] { 30 }, 3500), new('E', 2, new double[] { 9 }, 3500), new('E', 3, new double[] { 21 }, 3400), new('E', 4, new double[] { 14 }, 3400),
            new('E', 5, new double[] { 11 }, 3300), new('E', 6, new double[] { 8 }, 3300), new('E', 7, new double[] { 16 }, 3300), new('E', 8, new double[] { 6 }, 3300),
        },
    };

    private readonly float[] _previewGraph = new float[300];
    private readonly double[] _previewT = new double[300];
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
            _previewGraph[i] = (float)(7.0 + 0.6 * Math.Sin(k * 0.21) + ((k * 37) % 97 == 0 ? 12 : 0) + new Random(k).NextDouble() * 0.5);
            _previewT[i] = i == 0 ? 0 : _previewT[i - 1] + _previewGraph[i] / 1000.0;   // ov.15: the graph is plotted by time
        }
        var pv = OvPreview.View;   // ov.15: the HUD's values live on the shared view
        pv.Level = level;
        pv.Items = OverlayLevels.Items(level, _settings.OverlayLatencyFg, OvHidden);
        pv.Scale = look.Size / 100.0;
        pv.BgAlpha = look.Bg / 100.0;
        pv.Data = Sample;
        pv.Fps = 142 + rnd.Next(-2, 3);
        pv.FrameMs = 1000.0 / pv.Fps;
        // sample: frame gen doubling a 71 fps game, Reflex PC latency
        pv.GenFps = _settings.OverlayLatencyFg ? 71 : 0;
        if (_settings.OverlayLatencyFg) { pv.Fps = 71; pv.FrameMs = 1000.0 / 142; }
        pv.LatMs = 28 + rnd.Next(-2, 3);
        pv.LatPc = true;
        pv.Engine = "DX12";
        pv.Graph = _previewGraph;
        pv.GraphT = _previewT;
        pv.GraphCount = _previewGraph.Length;
        pv.GraphNow = _previewT[^1];
        pv.GraphRange = "min 6.4 / max 19.6 ms";
        pv.Clock = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        pv.Bound = "GPU";
        pv.Low1 = 98;
        pv.Low01 = 71;
        pv.PresentMode = "Hardware: Independent Flip";
        pv.MaxMs = 19.6;
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

    private void OvBg_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OvBgValue is null) return;
        OvBgValue.Text = $"{(int)e.NewValue}%";
        if ((int)e.NewValue == OvLook.Bg) return;
        // ov.11: lives in Settings now (Amine), one value for every level
        for (int lv = 1; lv <= OverlayLevels.Max; lv++) _settings.LookFor(lv).Bg = (int)e.NewValue;
        _settings.Save();
        ApplyOverlay();
    }

    // ov.12: Size / Move X / Move Y steppers (tags "Size", "X", "Y"; buttons "X:-1")
    private void PaintOverlaySteppers()
    {
        var l = OvLook;
        OvSizeValue.Text = $"{l.Size}%";
        OvXValue.Text = OffsetText(l.X);
        OvYValue.Text = OffsetText(l.Y);
    }

    private void OvStep_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string t }) return;
        var parts = t.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[1], out int dir)) StepLook(parts[0], dir);
        e.Handled = true;
    }

    private void OvStepWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key }) StepLook(key, Math.Sign(e.Delta));
        e.Handled = true;
    }

    private void OvStepReset(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || sender is not FrameworkElement { Tag: string key }) return;
        var l = OvLook;
        switch (key) { case "Size": l.Size = 100; break; case "X": l.X = 0; break; case "Y": l.Y = 0; break; }
        _settings.Save();
        PaintOverlaySteppers();
        ApplyOverlay();
        e.Handled = true;
    }

    private void StepLook(string key, int dir)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var l = OvLook;
        switch (key)
        {
            case "Size": l.Size = OverlayLook.StepSize(l.Size, dir, shift); break;
            case "X": l.X = OverlayLook.StepOffset(l.X, dir, shift); break;
            case "Y": l.Y = OverlayLook.StepOffset(l.Y, dir, shift); break;
            default: return;
        }
        _settings.Save();
        PaintOverlaySteppers();
        ApplyOverlay();
    }

    /// <summary>ov.9: the refresh and battery buttons of Show on overlay. Off = gone from every level and not read.
    /// ov.15: + clock and bound (levels 3 and 4, both off by default).</summary>
    private void OvShow_Click(object sender, RoutedEventArgs e)
    {
        _settings.OverlayShowRefresh = OvShowRefresh.IsChecked == true;
        _settings.OverlayShowBattery = OvShowBattery.IsChecked == true;
        _settings.OverlayShowClock = OvShowClock.IsChecked == true;
        _settings.OverlayShowBound = OvShowBound.IsChecked == true;
        _settings.Save();
        PaintShowSub();
        Log.Info("Overlay", $"show refresh {(_settings.OverlayShowRefresh ? "on" : "off")}, battery {(_settings.OverlayShowBattery ? "on" : "off")}, " +
            $"clock {(_settings.OverlayShowClock ? "on" : "off")}, bound {(_settings.OverlayShowBound ? "on" : "off")}");
        ApplyOverlay();
    }

    private void OvExtra_Click(object sender, RoutedEventArgs e)
    {
        _settings.OverlayLatencyFg = OvExtraToggle.IsChecked == true;
        _settings.Save();
        PaintShowSub();
        Log.Info("Overlay", $"latency and frame gen {(_settings.OverlayLatencyFg ? "on" : "off")}");
        ApplyOverlay();
    }

    /// <summary>ov.11: the Show on overlay sub line says what's on, like the mockup.</summary>
    private void PaintShowSub()
    {
        var on = new System.Collections.Generic.List<string>(5);
        if (_settings.OverlayLatencyFg) on.Add("latency");
        if (_settings.OverlayShowRefresh) on.Add("refresh");
        if (_settings.OverlayShowBattery) on.Add("battery");
        if (_settings.OverlayShowClock) on.Add("clock");
        if (_settings.OverlayShowBound) on.Add("bound");
        OvShowSub.Text = on.Count == 0 ? "All off: none of it is read" : "On: " + string.Join(", ", on);
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
