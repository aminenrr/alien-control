using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// 2.0.0-beta1.4 (ui review, MainWindow split): split out of MainWindow.xaml.cs, code moved unchanged. Sensor and state reads:
/// the 2 s sensor poll (temperatures, fans, mode), the Home numbers (RAM, VRAM, CPU %, GPU %) and the 1 s power line.
/// </summary>
public partial class MainWindow
{
    // ---------- reads ----------

    private DateTime _stateReadUtc = DateTime.MinValue;
    private bool _stateBusy;

    private bool _stateDirty, _stateDirtyFull;

    private async Task RefreshStateAsync(bool sensorsOnly = false)
    {
        // 1.2.0-beta.5: never stack CLI/ACPI reads. 2.0.0-beta1.3 (ui review #4): a request during a read is kept and
        // runs once after it (a click within ~1 s of opening lost its re-read)
        if (_stateBusy) { _stateDirty = true; _stateDirtyFull |= !sensorsOnly; return; }
        _stateBusy = true;
        try
        {
            while (true)
            {
                if (sensorsOnly) await RefreshSensorsAsync();
                else
                {
                    await RefreshStateCoreAsync();
                    _stateReadUtc = DateTime.UtcNow;
                }
                if (!_stateDirty) break;
                sensorsOnly = !_stateDirtyFull;
                _stateDirty = _stateDirtyFull = false;
            }
        }
        finally { _stateBusy = false; }
    }

    private async Task RefreshStateCoreAsync()
    {
        await RefreshSensorsAsync();

        var g = await _fan.GetGModeAsync();
        _gmodeOn = g == true;
        await SyncOcAsync();
        if (_gmodeOn) { ModeGMode.IsChecked = true; SetHint(PowerHint, "G-Mode"); return; }

        var raw = await _fan.GetPowerRawAsync();
        if (raw is { } value)
        {
            int idx = _settings.PowerRawByIndex.FirstOrDefault(kv => kv.Value == value).Key;
            if (idx > 0) _lastPowerIndex = idx;
            ModeBattery.IsChecked = idx == _settings.BatteryPowerIndex;
            ModeQuiet.IsChecked = idx == _settings.QuietPowerIndex;
            ModeBalanced.IsChecked = idx == _settings.BalancedPowerIndex;
            ModePerformance.IsChecked = idx == _settings.PerformancePowerIndex;
            SetHint(PowerHint, idx == _settings.BatteryPowerIndex ? "Battery"
                : idx == _settings.QuietPowerIndex ? "Quiet"
                : idx == _settings.BalancedPowerIndex ? "Balanced"
                : idx == _settings.PerformancePowerIndex ? "Performance"
                : $"Other mode ({value:x})");
        }
    }

    private async Task RefreshSensorsAsync()
    {
        if (_polling || !_fan.IsAvailable || !SensorsShown) return;   // 2.0: only read what is on screen
        _polling = true;
        try
        {
            var s = await _fan.ReadSensorsAsync();
            if (s.Error is not null) { Status(s.Error); return; }

            var cpu = s.Temps.FirstOrDefault(t => t.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));
            var gpu = s.Temps.FirstOrDefault(t => t.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase));
            SetTemp(CpuTempText, CpuTempBar, cpu);
            SetTemp(GpuTempText, GpuTempBar, gpu);
            TuneCpuTemp.Text = cpu is null ? "--" : $"{cpu.Celsius}°C";   // 2.0 Tuning live strip (same read)
            TuneGpuTemp.Text = gpu is null ? "--" : $"{gpu.Celsius}°C";

            if (s.Fans.Count > 0) _fanCount = s.Fans.Count;
            var cpuFan = s.Fans.FirstOrDefault(f => f.Type == "CPU") ?? s.Fans.ElementAtOrDefault(0);
            var gpuFan = s.Fans.FirstOrDefault(f => f.Type == "GPU") ?? s.Fans.ElementAtOrDefault(1);
            SetFanPercent(CpuFanText, CpuFanBar, cpuFan);
            SetFanPercent(GpuFanText, GpuFanBar, gpuFan);
            TuneFans.Text = $"{CpuFanText.Text} / {GpuFanText.Text} %";
            if (!HomePage.IsVisible) return;   // 2.0: RAM, VRAM, CPU % and GPU % are only on Home

            // 2.0.0-beta1.4 (ui review #5): RAM / VRAM / CPU % / GPU % are read on a worker thread (one PDH query, see
            // SystemStats); the UI thread only assigns the numbers
            bool igpu = _gpu.Kind == GpuModeKind.IGpuOnly;
            var h = await Task.Run(() => ReadHomeStats(igpu));
            if (!HomePage.IsVisible) return;
            if (h.Ram is { } ram) SetMem(RamText, RamTextUnit, RamBar, ram.Used, ram.Total);
            if (h.Vram is { } vram) SetMem(VramText, VramTextUnit, VramBar, vram.Used, vram.Total);
            // 1.3.0-beta.1 CPU % / GPU %
            if (h.Cpu is { } cpuUse) { CpuUseText.Text = cpuUse.ToString("0"); CpuUseBar.Value = cpuUse; }
            if (igpu) { GpuUseText.Text = "Off"; GpuUseUnit.Text = ""; GpuUseBar.Value = 0; }
            else if (h.Gpu is { } gpuUse) { GpuUseText.Text = gpuUse.ToString("0"); GpuUseUnit.Text = " %"; GpuUseBar.Value = gpuUse; }
        }
        finally { _polling = false; }
    }

    private readonly record struct HomeStats((double Used, double Total)? Ram, (double Used, double Total)? Vram, double? Cpu, double? Gpu);

    /// <summary>Worker thread: the Home numbers in one go. _stats has one caller at a time (RefreshSensorsAsync's
    /// _polling guard), which its CPU % delta needs.</summary>
    private HomeStats ReadHomeStats(bool igpuOnly) =>
        new(SystemStats.Ram(), _stats.Vram(), _stats.CpuUsage(), igpuOnly ? null : _stats.GpuUsage());

    // v0.8.5: power has its own 1 s timer (perf counter + NVML are cheap; the 2 s sensor poll reads WMI since 1.1, alienfan-cli as fallback).
    private bool _powerBusy;
    private async Task RefreshPowerAsync()
    {
        if (_powerBusy || !SensorsShown) return;
        _powerBusy = true;
        try { ShowPower(await Task.Run(_power.Sample)); }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("MainWindow.Sensors.cs:133", __e); }
        finally { _powerBusy = false; }
    }

    /// <summary>One power line; parts without a source are hidden, the line hides when none answers.</summary>
    private void ShowPower(PowerMeter.Reading r)
    {
        PwrCpu.Visibility = r.CpuW is null ? Visibility.Collapsed : Visibility.Visible;
        PwrGpu.Visibility = r.GpuW is null ? Visibility.Collapsed : Visibility.Visible;
        PwrTotal.Visibility = r.TotalW is null ? Visibility.Collapsed : Visibility.Visible;
        PwrCpuValue.Text = $"{r.CpuW} W";
        PwrGpuValue.Text = $"{r.GpuW} W";
        PwrTotalValue.Text = r.TotalEstimated ? $"~{r.TotalW} W" : $"{r.TotalW} W";
        TunePwr.Text = r.TotalW is not null ? PwrTotalValue.Text : r.CpuW is not null || r.GpuW is not null ? $"{(r.CpuW ?? 0) + (r.GpuW ?? 0)} W" : "--";
        PwrTotal.ToolTip = r.TotalEstimated
            ? "Estimate on AC: CPU + GPU + rest of the laptop (learned on battery)"
            : "Measured: battery discharge rate";
        PwrDot1.Visibility = r.CpuW is not null && (r.GpuW is not null || r.TotalW is not null) ? Visibility.Visible : Visibility.Collapsed;
        PwrDot2.Visibility = r.GpuW is not null && r.TotalW is not null ? Visibility.Visible : Visibility.Collapsed;
        PwrRow.Visibility = r.CpuW is null && r.GpuW is null && r.TotalW is null ? Visibility.Hidden : Visibility.Visible;   // 2.0: Hidden keeps Home's height
    }

    private static void SetTemp(Run text, ProgressBar bar, TempReading? t)
    {
        text.Text = t is null ? "--" : $"{t.Celsius}";
        bar.Value = t?.Celsius ?? 0;
    }

    /// <summary>Fan speed as % of the fan's max RPM (5000 on the m15 R7), capped at 100.</summary>
    private void SetFanPercent(Run text, ProgressBar bar, FanReading? f)
    {
        if (f is null) { text.Text = "--"; bar.Value = 0; return; }
        int pct = (int)Math.Round(Math.Clamp(f.Rpm * 100.0 / Math.Max(1, _settings.FanMaxRpm), 0, 100));
        text.Text = pct.ToString();
        bar.Value = pct;
        bar.ToolTip = $"{f.Rpm} rpm";
    }

    private static void SetMem(Run text, Run unit, ProgressBar bar, double used, double total)
    {
        text.Text = used.ToString("0.0");
        unit.Text = total > 0 ? $" / {Math.Round(total):0} GB" : " GB";
        bar.Value = total > 0 ? used * 100 / total : 0;
    }
}
