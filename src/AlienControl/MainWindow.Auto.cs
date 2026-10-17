using System;
using System.Threading.Tasks;
using System.Windows;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// v0.9.11 (Amine, 2026-10-15): auto preset on AC / battery + toasts.
/// Unplug: remember the AC preset (Quiet / Balanced / Performance; G-Mode or Custom -> Balanced), leave G-Mode,
/// apply Battery (the OC sync puts +0 back since we're off AC). Plug in: if still on Battery, bring the remembered
/// preset back; never re-enter G-Mode. No change at startup, only on power events. Battery is greyed on AC while on.
/// Toasts + sound only for F1, F2 and plug / unplug; window and tray clicks stay silent.
/// </summary>
public partial class MainWindow
{
    private bool _wasAc;

    /// <summary>Battery can't be picked on AC while auto preset is on (window + tray).</summary>
    public bool BatteryLocked => _settings.AutoPreset && OnAc;

    private void InitAuto()
    {
        _wasAc = OnAc;
        AutoPresetToggle.IsChecked = _settings.AutoPreset;
        ToastsToggle.IsChecked = _settings.Toasts;
        ToastSoundToggle.IsChecked = _settings.ToastSound;
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.StatusChange)
                Dispatcher.BeginInvoke(new Action(async () => await OnPowerLineAsync()));
        };
        ApplyBatteryLock();
    }

    private void ApplyBatteryLock()
    {
        ModeBattery.IsEnabled = !BatteryLocked;
        BatteryLock.Visibility = BatteryLocked ? Visibility.Visible : Visibility.Collapsed;
        ModeBattery.ToolTip = BatteryLocked ? "Battery comes on by itself when you unplug (Auto preset)" : null;
    }

    private bool _autoBusy;

    private async Task OnPowerLineAsync()
    {
        bool ac = OnAc;
        if (ac == _wasAc || _autoBusy) return;   // busy: the finally below re-checks   // StatusChange also fires for battery % changes
        _wasAc = ac;
        ApplyBatteryLock();
        UpdateKbdTimer();   // 1.3.0-beta.1: "On battery only" timeout
        if (!_settings.AutoPreset) return;
        _autoBusy = true;
        try
        {
            if (!ac)
            {
                bool wasG = _gmodeOn || await _fan.GetGModeAsync() == true;
                var cur = CurrentProfile;
                _settings.AcPreset = Profiles.RememberOnUnplug(cur, wasG).ToString();
                _settings.Save();
                if (!Profiles.SwitchOnUnplug(cur, wasG)) return;
                await ApplyProfileAsync(TrayProfile.Battery);
                Notify(new Toast.Info(TrayProfile.Battery, "Icon.Battery", "On battery",
                       wasG ? "G-Mode off · OC back to +0 · 60 Hz" : "Battery preset · 60 Hz · power saving", Toast.Right.Chip),
                       Sounds.Kind.Unplug);
            }
            else
            {
                if (Profiles.RestoreOnPlug(CurrentProfile, _gmodeOn, _settings.AcPreset) is not { } p) return;   // picked something else on battery: keep it
                await ApplyProfileAsync(p);
                Notify(new Toast.Info(p, "Icon.Plug", "Plugged in", $"Back to {Profiles.Label(p)} · 240 Hz", Toast.Right.Chip),
                       Sounds.Kind.Plug);
            }
        }
        finally { _autoBusy = false; }
        // 1.3.0-beta.1 (review #5): a quick unplug + replug during the switch was dropped; check the line again.
        if (OnAc != _wasAc) await OnPowerLineAsync();
    }

    /// <summary>Toast + sound, each with its own gear toggle. v0.9.12: after a refresh-rate change (Battery 60 Hz <-> 240 Hz)
    /// the screen blanks for about a second, so both wait until ~1.5 s after it and the toast stays a bit longer.</summary>
    private async void Notify(Toast.Info info, Sounds.Kind sound)
    {
        var since = (DateTime.UtcNow - _rateChangedUtc).TotalMilliseconds;
        bool modeSwitch = since < 1500;
        if (modeSwitch) await Task.Delay((int)(1500 - since));
        if (_settings.Toasts) Toast.Show(IsLegend, info, Motion.Enabled, modeSwitch ? 2200 : 1600);
        if (_settings.ToastSound) Sounds.Play(sound);
        if (!IsVisible) _ = TrimSoonAsync(_showGen);   // v0.9.13: the toast window's memory goes back once it's gone
    }

    /// <summary>v1.2: detail line of the F2 preset toast.</summary>
    private static string PresetSub(TrayProfile p) => p switch
    {
        TrayProfile.Quiet => "Efficient boost · 240 Hz · fans low",
        TrayProfile.Balanced => "CPU boost on · 240 Hz",
        TrayProfile.Performance => "Aggressive boost · 240 Hz · fans up",
        TrayProfile.Battery => "Boost off · 60 Hz · power saving",
        _ => "Your own settings",
    };

    private void AutoPreset_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoPreset = AutoPresetToggle.IsChecked == true;
        _settings.Save();
        ApplyBatteryLock();
    }

    private void Toasts_Click(object sender, RoutedEventArgs e)
    {
        _settings.Toasts = ToastsToggle.IsChecked == true;
        _settings.Save();
        UpdateSettingsDims();
    }

    private void ToastSound_Click(object sender, RoutedEventArgs e)
    {
        _settings.ToastSound = ToastSoundToggle.IsChecked == true;
        _settings.Save();
        UpdateSettingsDims();
        if (_settings.ToastSound) Sounds.Play(Sounds.Kind.Balanced);   // let them hear it
    }
}
