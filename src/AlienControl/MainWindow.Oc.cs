using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// v0.9.7 GPU overclock row (plan OK'd by Amine 2026-10-15). Offsets only run in G-Mode on AC
/// (1.3.4: or in the Performance preset on AC when the header Performance toggle is on):
/// Apply writes them and starts a 20 s keep-or-revert, Keep saves them, and from then on entering G-Mode on AC
/// applies them and leaving G-Mode, unplugging or quitting the app puts +0 back. No stress test, no voltage,
/// power limit or fan controls, and profiles never touch the GPU mode.
/// </summary>
public partial class MainWindow
{
    private const int OcTestSeconds = 20;
    private readonly DispatcherTimer _ocTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _ocLeft;
    private bool _ocTesting, _ocBusy, _ocInit;
    /// <summary>What the app last wrote to the driver; null = unknown (startup, after resume).</summary>
    private (int Core, int Mem)? _ocApplied;
    private string? _ocError;

    private static bool OnAc =>
        System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

    /// <summary>1.3.4: true when the Performance toggle is on and the Performance preset is running (not G-Mode).</summary>
    private bool OcPerfActive => _settings.OcInPerformance && !_gmodeOn && CurrentProfile == TrayProfile.Performance;

    private bool OcAllowed => OnAc && (_gmodeOn || OcPerfActive);

    /// <summary>Called once from the constructor.</summary>
    private void InitOc()
    {
        OcCoreSlider.Value = Math.Clamp(_settings.OcCore, 0, GpuOc.MaxUi);
        OcMemSlider.Value = Math.Clamp(_settings.OcMem, 0, GpuOc.MaxUi);
        _ocInit = true;
        _ocTimer.Tick += (_, _) =>
        {
            _ocLeft--;
            if (_ocLeft <= 0) _ = EndOcTestAsync(keep: false);
            else ShowOcCountdown();
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                _ocApplied = null;                 // a sleep can reset the driver: write again once it has settled
                await Task.Delay(3000);
                await OnResumeAsync();
            }
            if (e.Mode != Microsoft.Win32.PowerModes.Suspend) await SyncOcAsync();
        }));
        UpdateOcUi();
        Dispatcher.BeginInvoke(new Action(async () => await StartupOcAsync()));
    }

    private async Task StartupOcAsync()
    {
        if (_settings.OcPending)
        {
            // the app (or the driver) died during a test: never come back with offsets nobody kept
            _settings.OcPending = false;
            _settings.OcOn = false;
            _settings.Save();
            var err = await Task.Run(() => GpuOc.Set(0, 0));
            _ocApplied = err is null ? (0, 0) : null;
            Status(err ?? "GPU OC: the last test wasn't kept, clocks are back to stock.");
        }
        else if (!_settings.OcOn) _ocApplied = (0, 0);   // nothing was ever kept: the driver is at stock after boot
        _gmodeOn = await _fan.GetGModeAsync() == true;
        await SyncOcAsync();
    }

    private bool _boostForced;   // v0.9.9: G-Mode put CPU boost on Aggressive; undo when it ends
    private int? _preGBoost;      // AC boost level shown before G-Mode, for Custom

    private int? ShownBoost() =>
        CpuOff.IsChecked == true ? 0 : CpuOn.IsChecked == true ? 1 : CpuAggressive.IsChecked == true ? 2 : CpuEfficient.IsChecked == true ? 3 : null;

    /// <summary>v0.9.8/9: G-Mode side effects that follow every G-Mode change (same hook as OC). Header chip + tray icon
    /// show G-Mode; CPU boost goes to Aggressive (no cap) while it's on, and back to the preset's level when it ends.</summary>
    private async Task SyncGModeAsync()
    {
        if (_gmodeOn && _chipProfile != TrayProfile.GMode) SetProfile(TrayProfile.GMode);
        else if (!_gmodeOn && _chipProfile == TrayProfile.GMode) SetProfile(CurrentProfile);

        if (_gmodeOn && !_boostForced)
        {
            _boostForced = true;
            _preGBoost = ShownBoost();
            Report(await _fan.SetCpuBoostAsync(2, 2));
            if (await CpuFreq.ApplyAsync(2, 2) is { } err) Status(err);   // Aggressive = no cap
            ShowCpuBoost(2);
        }
        else if (!_gmodeOn && _boostForced)
        {
            _boostForced = false;
            await RestoreBoostAsync();
        }
    }

    /// <summary>The preset's boost (same on AC and battery), or for Custom the level shown before G-Mode (battery Efficient).</summary>
    private (int Ac, int Dc)? RestoreLevels() =>
        Profiles.CpuBoost(CurrentProfile) is { } b ? (b, b) : _preGBoost is { } pre ? (pre, 3) : null;

    private async Task RestoreBoostAsync()
    {
        if (RestoreLevels() is not { } lv) return;
        var (ac, dc) = lv;
        Report(await _fan.SetCpuBoostAsync(ac, dc));
        if (await CpuFreq.ApplyAsync(ac, dc) is { } err) Status(err);
        ShowCpuBoost(ac);
    }

    /// <summary>1.3.0-beta.1: everything Quit undoes before the process exits, async so the UI thread never blocks.
    /// Review #9: the Quiet 3.0 GHz cap is cleared from the power plan. Review #10: iGPU only offers to turn the RTX back on.</summary>
    public async Task PrepareExitAsync()
    {
        if (_gpu.Kind == GpuModeKind.IGpuOnly)
        {
            ((App)Application.Current).ShowMain(full: true);
            var pick = await ShowGpuDialog("The RTX is still off",
                "iGPU only stays on after Alien Controls quits (it survives restarts and uninstalling). Turn the RTX back on now?",
                null, null, ("keep", "Keep it off", false), ("on", "Turn it on", true));
            if (pick == "on") { _power.GpuOff = true; await GpuMode.SetDgpuEnabledAsync(true); }
        }
        if (_boostForced && RestoreLevels() is { } lv)
        {
            _boostForced = false;
            try { await _fan.SetCpuBoostAsync(lv.Ac, lv.Dc); } catch (Exception ex) { Services.Log.Warn("Exit boost", ex); }
        }
        try { await CpuFreq.ApplyAsync(1, 1); }   // no cap on AC or battery
        catch (Exception ex) { Services.Log.Warn("Exit CPU cap", ex); }
    }

    /// <summary>1.3.0-beta.1 (review #12): after sleep, check what the EC kept. The preset is re-applied only when the
    /// BIOS power mode no longer matches it; lighting and status keys are re-sent (one frame, cheap). Each finding goes
    /// to the local log so the checklist's resume probe can see what actually reset.</summary>
    private async Task OnResumeAsync()
    {
        try
        {
            _stateReadUtc = DateTime.MinValue;   // next open re-reads everything
            var raw = await _fan.GetPowerRawAsync();
            var want = CurrentProfile;
            int? wantIdx = want switch
            {
                TrayProfile.Battery => _settings.BatteryPowerIndex, TrayProfile.Quiet => _settings.QuietPowerIndex,
                TrayProfile.Balanced => _settings.BalancedPowerIndex, TrayProfile.Performance => _settings.PerformancePowerIndex, _ => null,
            };
            bool g = await _fan.GetGModeAsync() == true;
            bool presetLost = !g && wantIdx is int i && raw is int r && _settings.PowerRawByIndex.TryGetValue(i, out var exp) && exp != r;
            Services.Log.Info("Resume", $"preset {want}, power raw {(raw is int v ? v.ToString("x") : "?")}, G-Mode {g}, re-apply {presetLost}");
            if (presetLost) await ApplyProfileAsync(want);
            if (_lightsManaged) { _f2Dirty = true; await ApplyLightingAsync(_lastColor); }
            await SyncIndicatorsAsync();
        }
        catch (Exception ex) { Services.Log.Warn("Resume", ex); }
    }

    /// <summary>Synchronous boost restore for Quit when G-Mode forced Aggressive.</summary>
    private void RestoreBoostOnExit()
    {
        if (!_boostForced) return;
        _boostForced = false;
        if (RestoreLevels() is not { } lv) return;
        var (ac, dc) = lv;
        try { Task.Run(async () => { await _fan.SetCpuBoostAsync(ac, dc); await CpuFreq.ApplyAsync(ac, dc); }).Wait(4000); }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("MainWindow.Oc.cs:169", __e); /* exiting anyway */ }
    }

    /// <summary>Brings the driver in line with G-Mode + AC + the kept offsets. Cheap when nothing changes.</summary>
    private async Task SyncOcAsync()
    {
        if (!Dispatcher.CheckAccess()) { await Dispatcher.InvokeAsync(SyncOcAsync).Task.Unwrap(); return; }
        await SyncGModeAsync();
        // 1.2.0-beta.4: never touch NVML while the RTX is off or being switched (iGPU only)
        if (_rtxSwitching || _gpu.Kind == GpuModeKind.IGpuOnly) { UpdateOcUi(); return; }
        if (_ocTesting)
        {
            if (!OcAllowed) await EndOcTestAsync(keep: false);
            UpdateOcUi();
            return;
        }
        var want = _settings.OcOn && OcAllowed ? (Math.Clamp(_settings.OcCore, 0, GpuOc.MaxUi), Math.Clamp(_settings.OcMem, 0, GpuOc.MaxUi)) : (0, 0);
        if (_ocApplied != want && !_ocBusy)
        {
            _ocBusy = true;
            try
            {
                var err = await Task.Run(() => GpuOc.Set(want.Item1, want.Item2));
                _ocError = err;
                _ocApplied = err is null ? want : null;
                if (err is not null) Status(err);
            }
            finally { _ocBusy = false; }
        }
        UpdateOcUi();
    }

    /// <summary>Synchronous +0 for Quit (NVML takes a few ms).</summary>
    private void ResetOcOnExit()
    {
        _ocTimer.Stop();
        if (_ocTesting) { _settings.OcPending = false; _settings.Save(); }
        if (_ocApplied is (0, 0)) return;
        try { GpuOc.Set(0, 0); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("MainWindow.Oc.cs:207", __e); /* exiting anyway */ }
    }

    private void Oc_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OcCoreValue is null || OcMemValue is null) return;   // fires during InitializeComponent
        UpdateOcUi();
    }

    private async void OcApply_Click(object sender, RoutedEventArgs e)
    {
        if (_ocTesting) { await EndOcTestAsync(keep: true); return; }   // the button reads Keep during a test
        if (!OcAllowed || _ocBusy) return;
        int core = (int)OcCoreSlider.Value, mem = (int)OcMemSlider.Value;
        _ocBusy = true;
        try
        {
            // saved BEFORE the write: if anything dies from here on, the next launch goes back to +0
            _settings.OcPending = true;
            _settings.Save();
            var err = await Task.Run(() => GpuOc.Set(core, mem));
            if (err is not null)
            {
                _settings.OcPending = false;
                _settings.Save();
                _ocApplied = null;
                _ocError = err;
                Status(err);
                UpdateOcUi();
                return;
            }
            _ocError = null;
            _ocApplied = (core, mem);
            _ocTesting = true;
            _ocLeft = OcTestSeconds;
            _ocTimer.Start();
            UpdateOcUi();
        }
        finally { _ocBusy = false; }
    }

    private async void OcReset_Click(object sender, RoutedEventArgs e)
    {
        if (_ocTesting) { await EndOcTestAsync(keep: false); return; }   // the button reads Revert during a test
        _settings.OcOn = false;
        _settings.Save();
        await SyncOcAsync();
    }

    private async Task EndOcTestAsync(bool keep)
    {
        if (!_ocTesting) return;
        _ocTimer.Stop();
        _ocTesting = false;
        if (keep && _ocApplied is { } a && OcAllowed)
        {
            _settings.OcCore = a.Core;
            _settings.OcMem = a.Mem;
            _settings.OcOn = true;
        }
        else _settings.OcOn = false;
        _settings.OcPending = false;
        _settings.Save();
        await SyncOcAsync();   // Revert / timeout / left G-Mode: writes +0
    }

    /// <summary>1.3.4: Performance toggle in the section header. Saves, re-syncs (applies or drops the offsets now).</summary>
    private async void OcPerfToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.OcInPerformance = !_settings.OcInPerformance;
        _settings.Save();
        Status(_settings.OcInPerformance ? "GPU OC: on in G-Mode and Performance." : "GPU OC: G-Mode only.");
        await SyncOcAsync();
    }

    /// <summary>1.3.4: OC tag for a toast. scopeOk = the mode being entered runs the OC (G-Mode, or Performance with the
    /// toggle lit). On = kept offsets, on AC, not +0.</summary>
    private (string Text, bool On) OcToastTag(bool scopeOk)
    {
        bool on = scopeOk && _settings.OcOn && OnAc && (_settings.OcCore | _settings.OcMem) != 0;
        return on ? ($"OC +{_settings.OcCore} / +{_settings.OcMem}", true) : ("OC off", false);
    }

    private void ShowOcPerfToggle()
    {
        bool on = _settings.OcInPerformance;
        var t = Profiles.Tint(TrayProfile.Performance);
        var b = on ? new SolidColorBrush(Color.FromRgb(t.R, t.G, t.B)) : new SolidColorBrush(Color.FromArgb(150, 0x7F, 0x8B, 0x97));
        b.Freeze();
        OcPerfToggle.Foreground = b;
        OcPerfToggle.ToolTip = on
            ? "GPU OC runs in G-Mode and the Performance preset. Click for G-Mode only"
            : "GPU OC runs in G-Mode only. Click to also run it in the Performance preset";
    }

    private void ShowOcCountdown() =>
        OcCountdown.Text = IsLegend ? $"back to +0 in {_ocLeft} s" : $"Back to +0 in {_ocLeft} s";

    private void UpdateOcUi()
    {
        if (!_ocInit) return;
        int core = (int)OcCoreSlider.Value, mem = (int)OcMemSlider.Value;
        OcCoreValue.Text = $"+{core} MHz";
        OcMemValue.Text = $"+{mem} MHz";

        bool allowed = OcAllowed;
        OcBody.IsEnabled = allowed && !_ocTesting;
        OcBody.Opacity = allowed ? 1 : 0.45;
        OcApplyButton.IsEnabled = allowed;
        OcResetButton.IsEnabled = _ocTesting || _settings.OcOn || _ocApplied is not (0, 0);

        OcApplyButton.Content = _ocTesting ? "Keep" : "Apply";
        OcResetButton.Content = _ocTesting ? "Revert" : "Reset";
        OcConfirm.Visibility = _ocTesting ? Visibility.Visible : Visibility.Collapsed;
        OcActive.Visibility = _ocTesting ? Visibility.Collapsed : Visibility.Visible;
        if (_ocTesting) ShowOcCountdown();

        string active = _ocApplied is { } a && a != (0, 0) ? $"+{a.Core} / +{a.Mem}" : "+0 / +0";
        OcActive.Text = IsLegend ? $"ACTIVE {active}" : $"Active: {active}";

        ShowOcPerfToggle();
        bool perf = _settings.OcInPerformance;
        // 1.3.4-beta.2 (Amine): with the toggle lit the hint names both and turns the lighting (accent) colour
        if (perf) OcHint.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        else OcHint.ClearValue(TextBlock.ForegroundProperty);
        if (!_gmodeOn && !OcPerfActive) SetHint(OcHint, perf ? "G-Mode + Perf" : "G-Mode only");
        else if (!OnAc) SetHint(OcHint, "Plug in to use");
        else if (_ocError is not null) SetHint(OcHint, "Driver error");
        else if (_settings.OcOn) SetHint(OcHint, _gmodeOn ? "On in G-Mode" : "On in Performance");
        else SetHint(OcHint, "Off");
    }
}
