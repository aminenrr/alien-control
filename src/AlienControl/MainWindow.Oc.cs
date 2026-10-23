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
    private bool _ocDirty;        // 2.0.0-beta1.3 (ui review #4): a sync came in during a write; it runs once the write ends
    private bool _ocUnverified;   // 2.0.0-beta1.3 (services review #2): _ocApplied (0,0) is assumed; read the driver on AC
    /// <summary>What the app last wrote to the driver; null = unknown (startup, after resume).</summary>
    private (int Core, int Mem)? _ocApplied;
    private string? _ocError;

    private static bool OnAc =>
        System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

    /// <summary>1.3.4: true when the Performance toggle is on and the Performance preset is running (not G-Mode).</summary>
    private bool OcPerfActive => _settings.OcInPerformance && !_gmodeOn && CurrentProfile == TrayProfile.Performance;

    /// <summary>2.0.0-beta1.2: true when G-Mode is on and its scope switch (G-MODE) is lit.</summary>
    private bool OcGActive => _settings.OcInGMode && _gmodeOn;

    private bool OcAllowed => OnAc && (OcGActive || OcPerfActive);

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
            // 2.0.0-beta1.3: settings fell back to defaults (first start, reset, both files torn): the same +0, silent
            if (err is not null || !_settings.LoadedDefaults) Status(err ?? "GPU OC: the last test wasn't kept, clocks are back to stock.");
        }
        else if (!_settings.OcOn)
        {
            // nothing was ever kept: the driver is at stock after boot. 2.0.0-beta1.3 (services review #2): unless the
            // app died between saving OcOn=false and the +0 write, so SyncOcAsync reads the driver once on AC
            _ocApplied = (0, 0);
            _ocUnverified = true;
        }
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
    public async Task PrepareExitAsync(bool promptIgpu = true)
    {
        // 2.0.0-beta1.3 (ui review #3): Reset settings runs this too, without the iGPU prompt (the GPU mode stays as is)
        if (promptIgpu && _gpu.Kind == GpuModeKind.IGpuOnly)
        {
            ((App)Application.Current).ShowMain();
            var pick = await ShowGpuDialog("The RTX is still off",
                "iGPU only stays on after Alien Controls quits (it survives restarts and uninstalling). Turn the RTX back on now?",
                null, null, ("keep", "Keep it off", false), ("on", "Turn it on", true));
            if (pick == "on")
            {
                _power.GpuOff = true;
                // 2.0.0-beta1.3 (services review #1): no NVML call while pnputil re-adds the RTX
                await Task.Run(NvmlSession.BeginDeviceRemoval);
                try { await GpuMode.SetDgpuEnabledAsync(true); }
                finally { _ = EndDeviceRemovalWhenIdleAsync(); }
            }
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

    // 2.0.0-beta1.3 (ui review #3): RestoreBoostOnExit (a blocking .Wait(4000) on the UI thread) is gone: Quit and
    // Reset settings both await PrepareExitAsync, which restores the boost and clears the CPU cap.

    /// <summary>2.0.0-beta1.3 (services review #1): ends the NVML block once pnputil has really exited (one that timed
    /// out is left running, so NVML stays closed until it's done).</summary>
    private static async Task EndDeviceRemovalWhenIdleAsync()
    {
        try { await GpuMode.PnpIdle; } catch (Exception ex) { Services.Log.Warn("PnpIdle", ex); }
        NvmlSession.EndDeviceRemoval();
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
        if (_ocBusy) { _ocDirty = true; UpdateOcUi(); return; }   // 2.0.0-beta1.3: re-runs once the write in flight ends
        if (_ocUnverified && OnAc)
        {
            // 2.0.0-beta1.3 (services review #2): startup assumed stock; read what the driver really runs (AC only, so
            // a sleeping RTX isn't woken on battery). Can't read = keep the assumption, as before.
            _ocUnverified = false;
            _ocBusy = true;
            try
            {
                var cur = await Task.Run(GpuOc.Read);
                if (cur is { } c && c != (0, 0)) Services.Log.Info("GPU OC", $"driver runs +{c.Core} / +{c.Mem} at startup, settings say +0");
                if (cur is not null) _ocApplied = cur;
            }
            finally { _ocBusy = false; }
        }
        var want = _settings.OcOn && OcAllowed ? (Math.Clamp(_settings.OcCore, 0, GpuOc.MaxUi), Math.Clamp(_settings.OcMem, 0, GpuOc.MaxUi)) : (0, 0);
        if (_ocApplied != want)
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
        if (_ocDirty) { _ocDirty = false; await SyncOcAsync(); return; }   // e.g. unplugged during the write: +0 now
        UpdateOcUi();
    }

    /// <summary>Synchronous +0 for Quit (NVML takes a few ms).</summary>
    private void ResetOcOnExit()
    {
        _ocTimer.Stop();
        if (_ocTesting) { _settings.OcPending = false; _settings.Save(); }
        if (_ocApplied is (0, 0)) return;
        // 2.0.0-beta1.3: bounded wait for the shared NVML lock, so a stuck driver call can't hang the exit
        try { GpuOc.Set(0, 0, lockTimeoutMs: 2000); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("MainWindow.Oc.cs:207", __e); /* exiting anyway */ }
    }

    private int _emergencyDone;

    /// <summary>2.0.0-beta1.3 (services review #2): best-effort cleanup when the app is about to die (unhandled crash)
    /// or Windows ends the session: OC +0 when the app wrote anything else, and the Efficient CPU cap cleared from the
    /// power plan. Synchronous and bounded (one worker thread, joined for at most joinMs), callable from any thread.
    /// Runs once. Only ever writes +0 / no cap.</summary>
    public void EmergencyExitCleanup(int joinMs)
    {
        if (System.Threading.Interlocked.Exchange(ref _emergencyDone, 1) == 1) return;
        var applied = _ocApplied;
        bool rtxOff = _gpu.Kind == GpuModeKind.IGpuOnly || _rtxSwitching;   // never touch NVML then (NvmlSession refuses during a switch too)
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                if (applied is not (0, 0) && !rtxOff)
                {
                    var err = GpuOc.Set(0, 0, lockTimeoutMs: 1500);
                    Services.Log.Info("Exit cleanup", err ?? "GPU OC +0");
                }
            }
            catch (Exception ex) { Services.Log.Warn("Exit cleanup OC", ex); }
            try { if (CpuFreq.Clear() is { } e) Services.Log.Info("Exit cleanup", e); }
            catch (Exception ex) { Services.Log.Warn("Exit cleanup CPU cap", ex); }
        }) { IsBackground = true, Name = "AlienControls exit cleanup" };
        t.Start();
        if (!t.Join(joinMs)) Services.Log.Info("Exit cleanup", $"not done after {joinMs} ms, exiting anyway");
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
        finally
        {
            _ocBusy = false;
            // 2.0.0-beta1.3 (ui review #4): a sync that came in during the write (unplug, G-Mode off) runs now
            if (_ocDirty) { _ocDirty = false; _ = Dispatcher.BeginInvoke(new Action(async () => await SyncOcAsync())); }
        }
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

    /// <summary>2.0.0-beta1.2 (Amine): scope pill in the section header with two separate switches. G-MODE (OcInGMode)
    /// = the kept offsets run in G-Mode on AC, + PERF (OcInPerformance, the 1.3.4 toggle) = they run in the Performance
    /// preset on AC. Either one, both, or neither (no overclock at all). Each click flips its own switch, saves, re-syncs.</summary>
    private async void OcScope_Click(object sender, RoutedEventArgs e)
    {
        if (sender == OcScopePerf) _settings.OcInPerformance = !_settings.OcInPerformance;
        else _settings.OcInGMode = !_settings.OcInGMode;
        _settings.Save();
        Status(OcScopeText() + ".");
        await SyncOcAsync();
        UpdateOcUi();
    }

    private string OcScopeText() => (_settings.OcInGMode, _settings.OcInPerformance) switch
    {
        (true, true) => "GPU OC: on in G-Mode and Performance",
        (true, false) => "GPU OC: G-Mode only",
        (false, true) => "GPU OC: Performance only",
        _ => "GPU OC: off (G-MODE and + PERF are both off)",
    };

    /// <summary>1.3.4: OC tag for a toast. scopeOk = the mode being entered runs the OC (G-Mode, or Performance with the
    /// toggle lit). On = kept offsets, on AC, not +0.</summary>
    private (string Text, bool On) OcToastTag(bool scopeOk)
    {
        bool on = scopeOk && _settings.OcOn && OnAc && (_settings.OcCore | _settings.OcMem) != 0;
        return on ? ($"OC +{_settings.OcCore} / +{_settings.OcMem}", true) : ("OC off", false);
    }

    private void ShowOcPerfToggle()
    {
        OcScopeG.IsChecked = _settings.OcInGMode;
        OcScopePerf.IsChecked = _settings.OcInPerformance;
        OcScopeTrack.ToolTip = OcScopeText() + " (on AC). G-MODE and + PERF switch on and off on their own; both off = no overclock";
    }

    private void ShowOcCountdown() =>
        OcCountdown.Text = $"BACK TO +0 IN {_ocLeft} S";

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
        OcActivePanel.Visibility = _ocTesting ? Visibility.Collapsed : Visibility.Visible;
        if (_ocTesting) ShowOcCountdown();

        string active = _ocApplied is { } a && a != (0, 0) ? $"+{a.Core} / +{a.Mem}" : "+0 / +0";
        OcActive.Text = $"ACTIVE {active}";
        UpdateHomeGModeSub();   // 2.0: the Home G-Mode card names the kept overclock

        ShowOcPerfToggle();
        bool g = _settings.OcInGMode, perf = _settings.OcInPerformance;
        // 1.3.4-beta.2 (Amine): with + PERF lit the hint names both and turns the lighting (accent) colour
        if (perf) OcHint.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        else OcHint.ClearValue(TextBlock.ForegroundProperty);
        if (!g && !perf) SetHint(OcHint, "No overclock");
        else if (!OcGActive && !OcPerfActive) SetHint(OcHint, g && perf ? "G-Mode + Perf" : g ? "G-Mode only" : "Perf only");
        else if (!OnAc) SetHint(OcHint, "Plug in to use");
        else if (_ocError is not null) SetHint(OcHint, "Driver error");
        else if (_settings.OcOn) SetHint(OcHint, OcGActive ? "On in G-Mode" : "On in Performance");
        else SetHint(OcHint, "Off");
    }
}
