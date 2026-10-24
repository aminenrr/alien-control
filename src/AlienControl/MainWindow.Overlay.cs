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
    private OverlayHost? _overlay;
    private Need _ovNeed;
    private Sensors _ovSnap = new();
    private readonly CpuCores _cores = new();
    private readonly DispatcherTimer _ovExtra = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _ovPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _ovExtraBusy;
    private int _ovRefreshTick;

    private bool OvNeeds(Need n) => (_ovNeed & n) != 0;

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
        OvSizeSlider.Value = _settings.OverlaySize;
        OvBgSlider.Value = _settings.OverlayBg;
        (_settings.OverlayUpdateMs switch { 250 => OvUpd250, 1000 => OvUpd1000, _ => OvUpd500 }).IsChecked = true;
        PaintOverlayPosition();
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
        4 => "Everything: per thread CPU, fans, refresh rate",
        _ => "Off",
    };

    private static string LevelItems(int level) => level switch
    {
        1 => "FPS.",
        2 => "FPS, frametime and graph, CPU and GPU load and watts, RAM, VRAM, battery.",
        3 => "GPU load, temp, watts, core and memory clock. CPU load, temp, watts, clock. VRAM, RAM, battery, engine, FPS, frametime graph.",
        4 => "Level 3 plus load per CPU thread, fan RPM, refresh rate, 1% low and max frametime.",
        _ => "Nothing runs: PresentMon is stopped and no sensor is read for the overlay.",
    };

    /// <summary>Brings everything in line with settings: frames source, overlay window, extra timer, pollers, UI.</summary>
    private void ApplyOverlay()
    {
        int level = _settings.OverlayLevel;
        var oldNeed = _ovNeed;
        _ovNeed = OverlayLevels.Needs(level);

        if (level > 0) { if (!_frames.Running) _frames.Start(); }
        else _frames.Stop();

        _overlay ??= level > 0 ? new OverlayHost(_frames) : null;
        _overlay?.Apply(level, _settings.OverlayPosition, _settings.OverlaySize / 100.0 * 1.0, _settings.OverlayBg / 100.0, _settings.OverlayUpdateMs);

        bool extra = OvNeeds(Need.Clocks) || OvNeeds(Need.Cores) || OvNeeds(Need.Battery) || OvNeeds(Need.Refresh);
        if (extra) { if (!_ovExtra.IsEnabled) { _ovExtra.Start(); _ = OverlayExtraTickAsync(); } }
        else _ovExtra.Stop();

        // the NVML session the clocks kept open: give it back when nothing else (the power readout) needs it
        if ((oldNeed & Need.Clocks) != 0 && !OvNeeds(Need.Clocks) && !SensorsShown && !OvNeeds(Need.Power)) NvmlSession.Release();
        if (level == 0) _ovSnap = new Sensors();

        UpdatePolling();
        if (level > 0 && !SensorsShown) _ = Dispatcher.BeginInvoke(new Action(async () => { await RefreshSensorsAsync(); await RefreshPowerAsync(); }), DispatcherPriority.Background);

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
                int? mhz = (need & Need.Clocks) != 0 ? _cores.Mhz() : null;
                (int? core, int? mem) gpu = (need & Need.Clocks) != 0 && gpuOn ? GpuClocks() : (null, null);
                double[]? cores = (need & Need.Cores) != 0 ? _cores.Loads() : null;
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
                RefreshHz = r.hz ?? _ovSnap.RefreshHz,
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
        Cores = new double[] { 62, 40, 55, 31, 48, 22, 70, 35, 44, 18, 25, 12, 30, 9, 21, 14, 11, 8, 16, 6 },
    };

    private readonly float[] _previewGraph = new float[160];
    private int _previewPhase;

    private void UpdateOverlayPreview()
    {
        if (OvPreview is null || !OverlayPage.IsVisible) return;
        int level = _settings.OverlayLevel > 0 ? _settings.OverlayLevel : Math.Clamp(_settings.OverlayLastLevel, 1, OverlayLevels.Max);
        _previewPhase++;
        var rnd = new Random(_previewPhase);
        for (int i = 0; i < _previewGraph.Length; i++)
        {
            int k = i + _previewPhase * 4;
            _previewGraph[i] = (float)(7.0 + 0.6 * Math.Sin(k * 0.21) + ((k * 37) % 97 == 0 ? 5 : 0) + new Random(k).NextDouble() * 0.5);
        }
        OvPreview.Level = level;
        OvPreview.Items = OverlayLevels.Items(level);
        OvPreview.Scale = _settings.OverlaySize / 100.0;
        OvPreview.BgAlpha = _settings.OverlayBg / 100.0;
        OvPreview.Data = Sample;
        OvPreview.Fps = 142 + rnd.Next(-2, 3);
        OvPreview.FrameMs = 1000.0 / OvPreview.Fps;
        OvPreview.Engine = "DX12";
        OvPreview.Graph = _previewGraph;
        OvPreview.GraphCount = _previewGraph.Length;
        OvPreview.Low1 = 98;
        OvPreview.MaxMs = 12.4;
        OvPreview.InvalidateMeasure();
        OvPreview.InvalidateVisual();
        int pos = _settings.OverlayPosition;
        OvPreviewBox.HorizontalAlignment = (pos % 3) switch { 0 => HorizontalAlignment.Left, 1 => HorizontalAlignment.Center, _ => HorizontalAlignment.Right };
        OvPreviewBox.VerticalAlignment = pos < 3 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
    }

    private void PaintOverlayPosition()
    {
        foreach (Border b in OvPosGrid.Children)
        {
            bool on = int.TryParse(b.Tag?.ToString(), out int i) && i == _settings.OverlayPosition;
            if (on) b.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            else b.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x21, 0x27));
        }
        OvPosText.Text = _settings.OverlayPosition switch
        {
            0 => "Top left, like SteamOS", 1 => "Top centre", 2 => "Top right",
            3 => "Bottom left", 4 => "Bottom centre", _ => "Bottom right",
        };
    }

    private void OvPos_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: var t } || !int.TryParse(t?.ToString(), out int pos)) return;
        _settings.OverlayPosition = Math.Clamp(pos, 0, 5);
        _settings.Save();
        PaintOverlayPosition();
        ApplyOverlay();
    }

    private void OvSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OvSizeValue is null) return;   // during InitializeComponent
        OvSizeValue.Text = $"{(int)e.NewValue}%";
        if ((int)e.NewValue == _settings.OverlaySize) return;
        _settings.OverlaySize = (int)e.NewValue;
        _settings.Save();
        ApplyOverlay();
    }

    private void OvBg_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OvBgValue is null) return;
        OvBgValue.Text = $"{(int)e.NewValue}%";
        if ((int)e.NewValue == _settings.OverlayBg) return;
        _settings.OverlayBg = (int)e.NewValue;
        _settings.Save();
        ApplyOverlay();
    }

    private void OvUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: var t } || !int.TryParse(t?.ToString(), out int ms)) return;
        _settings.OverlayUpdateMs = ms;
        _settings.Save();
        ApplyOverlay();
    }

    private void OvReset_Click(object sender, MouseButtonEventArgs e)
    {
        var d = new Settings();
        _settings.OverlayPosition = d.OverlayPosition;
        _settings.OverlaySize = d.OverlaySize;
        _settings.OverlayBg = d.OverlayBg;
        _settings.OverlayUpdateMs = d.OverlayUpdateMs;
        _settings.Save();
        OvSizeSlider.Value = d.OverlaySize;
        OvBgSlider.Value = d.OverlayBg;
        OvUpd500.IsChecked = true;
        PaintOverlayPosition();
        ApplyOverlay();
        Status("Overlay look reset to SteamOS.");
    }

    /// <summary>Every exit path (Quit, Reset, crash cleanup, Windows ending the session): stop PresentMon and close the
    /// overlay window. The job object kills PresentMon even when this never runs.</summary>
    private void StopOverlayOnExit()
    {
        try { _ovExtra.Stop(); _ovPreviewTimer.Stop(); } catch { /* wrong thread on the crash path */ }
        try { _frames.Stop(); } catch (Exception e) { Log.Warn("Overlay exit frames", e); }
        try { _overlay?.Dispose(); } catch (Exception e) { Log.Warn("Overlay exit window", e); }
    }
}
