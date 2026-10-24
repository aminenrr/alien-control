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
/// 2.0.0-beta1.4 (ui review, MainWindow split): split out of MainWindow.xaml.cs, code moved unchanged. Power modes and G-Mode
/// under the one command gate (_cmdGate), F1, CPU boost, fan boost and the 60 / 240 Hz refresh rate buttons.
/// </summary>
public partial class MainWindow
{
    // ---------- actions ----------

    // 2.0.0-beta1.3 (ui review #4): one gate for every G-Mode, preset and power-mode command (F1, the Home switch, the
    // Tuning mode radios, ApplyProfileAsync from the tray, Home tiles, header, F2, auto preset and resume), so two
    // can't interleave their CLI, powercfg and refresh-rate calls and leave the state from the other one.
    private readonly System.Threading.SemaphoreSlim _cmdGate = new(1, 1);
    /// <summary>The G-Mode state the running (or queued) change is heading to; null when none runs.</summary>
    private bool? _gmodeTarget;
    private int _gmodePending;

    private async void Mode_Click(object sender, RoutedEventArgs e)
    {
        await _cmdGate.WaitAsync();
        try
        {
            CliResult r;
            if (sender == ModeGMode)
            {
                r = await _fan.SetGModeAsync(true);
                _gmodeOn = true;
                SetHint(PowerHint, "G-Mode");
            }
            else
            {
                if (_gmodeOn) await _fan.SetGModeAsync(false);
                int idx = sender == ModeBattery ? _settings.BatteryPowerIndex
                        : sender == ModeQuiet ? _settings.QuietPowerIndex
                        : sender == ModeBalanced ? _settings.BalancedPowerIndex
                        : _settings.PerformancePowerIndex;
                r = await _fan.SetPowerAsync(idx);
                _gmodeOn = false;
                _lastPowerIndex = idx;
                SetHint(PowerHint, ((RadioButton)sender).Content?.ToString() ?? "");
            }
            Report(r);
            UpdateProfileFromState();
            await SyncOcAsync();
            await SyncIndicatorsAsync();
        }
        finally { _cmdGate.Release(); }
    }

    /// <summary>G-key press: toggle G-Mode; turning it off returns to the last power mode.</summary>
    private async Task ToggleGModeAsync()
    {
        // v1.0.0-rc1: toast + sound right on the key press (they used to wait for the CLI calls, ~1 s).
        // _gmodeOn is kept in sync by every read, so no extra getgmode call before acting.
        // 2.0.0-beta1.3 (ui review #4): a press while a change runs (F1 or the Home switch) toggles the state that change
        // is heading to and runs after it, so a quick second F1 means off and never sends "on" twice.
        bool want = !(_gmodeTarget ?? _gmodeOn);
        if (want)
        {
            var (oc, ocLit) = OcToastTag(_settings.OcInGMode);   // 2.0.0-beta1.2: G-MODE switch off = "OC off"
            Notify(new Toast.Info(TrayProfile.GMode, "Icon.GMode", "G-Mode on", "Fans max", Toast.Right.SwitchOn, Oc: oc, OcLit: ocLit),
                Sounds.Kind.GModeOn);
        }
        else
            Notify(new Toast.Info(CurrentProfile, "Icon.GMode", "G-Mode off", $"Back to {Profiles.Label(CurrentProfile)} · fans auto",
                Toast.Right.SwitchOff, Lit: false), Sounds.Kind.GModeOff);
        await SetGModeAsync(want, fromKey: true);
    }

    /// <summary>2.0.0-beta1.3: the one G-Mode on/off path for F1 and the Home switch. Both mark the change busy (the
    /// Home switch greys out), run under the command gate, and keep their own follow-up order from 2.0.0-beta1.2.</summary>
    private async Task SetGModeAsync(bool want, bool fromKey)
    {
        _gmodeTarget = want;
        _gmodePending++;
        _gmodeUiBusy = true;
        HomeGModeToggle.IsEnabled = false;
        try
        {
            if (fromKey) await Task.Delay(150);   // 1.3.0: like F2, the chime's attack plays before the ACPI calls start (crackle)
            await _cmdGate.WaitAsync();
            try
            {
                if (want == _gmodeOn) return;   // a queued press whose state is already there
                if (want)
                {
                    Report(await _fan.SetGModeAsync(true));
                    _gmodeOn = true;
                }
                else
                {
                    await _fan.SetGModeAsync(false);
                    Report(await _fan.SetPowerAsync(_lastPowerIndex));
                    _gmodeOn = false;
                }
                if (fromKey)
                {
                    if (IsVisible) await RefreshStateAsync();
                    UpdateProfileFromState();
                    await SyncOcAsync();
                    await SyncIndicatorsAsync();
                }
                else
                {
                    UpdateProfileFromState();
                    await SyncOcAsync();
                    await SyncIndicatorsAsync();
                    if (IsVisible) await RefreshStateAsync();
                }
            }
            finally { _cmdGate.Release(); }
        }
        finally
        {
            if (--_gmodePending == 0)
            {
                _gmodeTarget = null;
                _gmodeUiBusy = false;
                HomeGModeToggle.IsEnabled = true;
            }
            UpdateHomeState();
        }
    }

    private void GKeyDetect_Click(object sender, RoutedEventArgs e)
    {
        GKeyText.Text = "Press the G-key (F1) now…";
        _gkey.LearnNext = (vk, scan) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _settings.GKeyVk = _gkey.Vk = vk;
            _settings.GKeyScan = _gkey.Scan = scan;
            _settings.Save();
            UpdateGKeyText();
            Status($"G-key set to key 0x{vk:X2} (scan 0x{scan:X2}).");
        }));
    }

    private void UpdateGKeyText() =>
        GKeyText.Text = $"Key 0x{_settings.GKeyVk:X2}";

    private async void Cpu_Click(object sender, RoutedEventArgs e)
    {
        // AC level from the button; battery stays on "efficient" (3).
        int level = int.Parse((string)((RadioButton)sender).CommandParameter);
        Report(await _fan.SetCpuBoostAsync(level, 3));
        if (await CpuFreq.ApplyAsync(level, 3) is { } err) Status(err);   // v0.9.4: Efficient = 3.0 GHz cap (v0.9.5)
        _boostForced = false;   // v0.9.9: an explicit pick wins, even inside G-Mode
    }

    private async void Fan_Click(object sender, RoutedEventArgs e)
    {
        int boost = sender == FanMax ? _settings.MaxFanBoost : 0;
        Report(await _fan.SetFanBoostAsync(Enumerable.Repeat(boost, _fanCount).ToArray()));
        if (boost == 0) FanHint.Text = "BIOS";
        else SetHint(FanHint, $"Boost {boost}%");
    }


    // ---------- refresh rate (read on window show and after a change; never polled) ----------

    private void RefreshDisplayRate()
    {
        var s = DisplayRefresh.Read();
        if (s is null)
        {
            Hz60.IsEnabled = Hz240.IsEnabled = false;
            SetHint(RefreshHint, "Unknown");
            return;
        }
        Hz60.IsEnabled = DisplayRefresh.Match(s, 60) is not null;
        Hz240.IsEnabled = DisplayRefresh.Match(s, 240) is not null;
        Hz60.IsChecked = DisplayRefresh.IsActive(s, 60);
        Hz240.IsChecked = DisplayRefresh.IsActive(s, 240);
        RefreshHint.Text = $"{s.CurrentHz} Hz";   // 2.0 mock: "240 Hz"
        Motion.Fps = Math.Clamp(s.CurrentHz, 30, 240);   // 1.3.0-beta.1: storyboards follow the panel
        Hz60.ToolTip = Hz60.IsEnabled ? null : $"60 Hz isn't offered at {s.Width}x{s.Height}";
        Hz240.ToolTip = Hz240.IsEnabled ? null : $"240 Hz isn't offered at {s.Width}x{s.Height}";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        int hz = sender == Hz60 ? 60 : 240;
        var err = await Task.Run(() => DisplayRefresh.Set(hz));
        Status(err ?? $"Display set to {hz} Hz.");
        RefreshDisplayRate();
        UpdateProfileFromState();
    }
}
