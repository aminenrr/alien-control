using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// 2.0 (docs/mockups/2.0/HANDOFF.md): the window is one fixed 440 x 820 card with four tab pages, HOME, TUNING, LIGHTS
/// and OVERLAY (beta 2). Compact mode is gone. Only what is on screen is read: the sensor and power timers run while
/// Home or Tuning is shown and the window is visible, RAM / VRAM / CPU % / GPU % only on Home. The header preset
/// button is hidden on Home (the presets are on the page) and shown on the other tabs.
/// </summary>
public partial class MainWindow
{
    private bool _gmodeUiBusy;

    /// <summary>True when the visible page shows sensors (Home or Tuning) and the settings page is closed.</summary>
    private bool SensorsShown => IsVisible && !SettingsShown && (HomePage.Visibility == Visibility.Visible || TuningPage.Visibility == Visibility.Visible);

    private void InitTabs()
    {
        // Home preset tiles use the preset colours of the tray and the header button
        foreach (var (rb, p) in HomeTiles())
        {
            var t = Profiles.Tint(p);
            var b = new SolidColorBrush(Color.FromRgb(t.R, t.G, t.B));
            b.Freeze();
            rb.BorderBrush = b;
        }
        ShowPage(HomePage);
    }

    private (RadioButton Tile, TrayProfile Profile)[] HomeTiles() => new[]
    {
        (HomeBattery, TrayProfile.Battery), (HomeQuiet, TrayProfile.Quiet), (HomeBalanced, TrayProfile.Balanced),
        (HomePerformance, TrayProfile.Performance), (HomeCustom, TrayProfile.Custom),
    };

    private Grid[] Pages => new[] { HomePage, TuningPage, LightsPage, OverlayPage };
    private RadioButton[] PageTabs => new[] { TabHome, TabTuning, TabLights, TabOverlay };

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        int i = Array.IndexOf(PageTabs, sender as RadioButton);
        if (i >= 0) ShowPage(Pages[i]);
    }

    private void CycleTab(int step)
    {
        int i = Array.IndexOf(Pages, Array.Find(Pages, p => p.Visibility == Visibility.Visible));
        ShowPage(Pages[((i < 0 ? 0 : i) + step + Pages.Length) % Pages.Length]);
    }

    private void ShowPage(Grid page)
    {
        foreach (var p in Pages) p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        PageTabs[Array.IndexOf(Pages, page)].IsChecked = true;
        PresetButton.Visibility = page == HomePage ? Visibility.Collapsed : Visibility.Visible;
        if (page == HomePage) PresetPopup.IsOpen = false;
        if (page != LightsPage && page != HomePage) CustomPopup.IsOpen = false;
        UpdatePolling();
        // fresh values right away on a sensor page (the timer would take up to 2 s)
        if (SensorsShown) Dispatcher.BeginInvoke(new Action(async () => await RefreshSensorsAsync()), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Starts the sensor and power timers only while a sensor page is on screen; stops them otherwise.</summary>
    private void UpdatePolling()
    {
        if (SensorsShown && !_openParked)
        {
            if (!_poll.IsEnabled) _poll.Start();
            if (!_powerTimer.IsEnabled && !_rtxSwitching) _powerTimer.Start();
        }
        else
        {
            _poll.Stop();
            _powerTimer.Stop();
        }
    }

    // ---------- Home: presets, G-Mode card ----------

    /// <summary>Home preset tile: the same path as the tray menu and the header list (silent, no toast).</summary>
    private async void HomePreset_Click(object sender, RoutedEventArgs e)
    {
        var p = Array.Find(HomeTiles(), t => t.Tile == sender).Profile;
        if (p == ShownProfile && p == CurrentProfile) return;
        await ApplyProfileAsync(p);
    }

    /// <summary>Home G-Mode switch: the window version of F1 (no toast or sound, like every window click).
    /// Off goes back to the last power mode, as F1 does.</summary>
    private async void HomeGMode_Click(object sender, RoutedEventArgs e)
    {
        bool want = HomeGModeToggle.IsChecked == true;
        if (_gmodeUiBusy || want == _gmodeOn) { UpdateHomeState(); return; }
        _gmodeUiBusy = true;
        HomeGModeToggle.IsEnabled = false;
        try
        {
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
            UpdateProfileFromState();
            await SyncOcAsync();
            await SyncIndicatorsAsync();
            if (IsVisible) await RefreshStateAsync();
        }
        finally
        {
            _gmodeUiBusy = false;
            HomeGModeToggle.IsEnabled = true;
            UpdateHomeState();
        }
    }

    /// <summary>Home follows the state: preset tile (none in G-Mode), G-Mode switch, F2 hint, Battery lock.</summary>
    private void UpdateHomeState()
    {
        if (HomePresetHint is null) return;
        var shown = ShownProfile;
        foreach (var (rb, p) in HomeTiles()) rb.IsChecked = !_gmodeOn && p == shown;
        if (!_gmodeUiBusy) HomeGModeToggle.IsChecked = _gmodeOn;
        HomePresetHint.Text = "F2 · " + Profiles.Label(shown).ToUpperInvariant();
        HomeBattery.IsEnabled = !BatteryLocked;
        HomeBatteryLock.Visibility = BatteryLocked ? Visibility.Visible : Visibility.Collapsed;
        HomeBattery.ToolTip = BatteryLocked ? "Battery comes on by itself when you unplug (Auto preset)" : Profiles.Describe(TrayProfile.Battery);
    }

    /// <summary>Second line of the Home G-Mode card: the kept GPU overclock, or that there is none.</summary>
    private void UpdateHomeGModeSub()
    {
        if (HomeGModeSub is null) return;
        bool oc = _settings.OcOn && (_settings.OcCore | _settings.OcMem) != 0;
        // 2.0.0-beta1.2: the G-MODE / + PERF switches decide where the kept overclock runs
        HomeGModeSub.Text = !oc ? "No GPU overclock kept (Tuning)"
            : !_settings.OcInGMode && !_settings.OcInPerformance ? "GPU overclock off (Tuning)"
            : !_settings.OcInGMode ? "GPU overclock in Performance only (Tuning)"
            : $"GPU overclock +{_settings.OcCore} / +{_settings.OcMem} on AC";
    }

    // ---------- 2.0: F4 is reserved for the overlay (beta 2) ----------

    private long _f4Last;

    private void OnF4Pressed()
    {
        if (!_settings.F4Overlay) return;
        long now = Environment.TickCount64;
        if (now - _f4Last < 1500) return;   // one hint per burst of presses
        _f4Last = now;
        Log.Info("F4", "overlay key pressed (overlay comes in beta 2)");
        if (_settings.Toasts)
            Toast.Show(true, new Toast.Info(CurrentProfile, "Icon.ChartBar", "Overlay", "Comes in beta 2", Toast.Right.None, Lit: false,
                Accent: (Application.Current.Resources["AccentBrush"] as SolidColorBrush)?.Color), Motion.Enabled);
    }
}
