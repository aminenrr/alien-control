using System;
using AlienControl.Overlay;
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

    /// <summary>True when the visible page shows sensors (Home or Tuning) and the settings page is closed.
    /// 2.0.0-beta1.3 (ui review #2): and the window isn't minimized (a minimized window still reports IsVisible).</summary>
    private bool SensorsShown => IsVisible && WindowState != WindowState.Minimized && !SettingsShown
        && (HomePage.Visibility == Visibility.Visible || TuningPage.Visibility == Visibility.Visible);

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

    // 2.0.0-beta1.4 (ui review #21): built once (they were new arrays on every access)
    private (RadioButton Tile, TrayProfile Profile)[]? _homeTiles;
    private (RadioButton Tile, TrayProfile Profile)[] HomeTiles() => _homeTiles ??= new[]
    {
        (HomeBattery, TrayProfile.Battery), (HomeQuiet, TrayProfile.Quiet), (HomeBalanced, TrayProfile.Balanced),
        (HomePerformance, TrayProfile.Performance), (HomeCustom, TrayProfile.Custom),
    };

    private Grid[]? _pages;
    private RadioButton[]? _pageTabs;
    private Grid[] Pages => _pages ??= new[] { HomePage, TuningPage, LightsPage, OverlayPage };
    private RadioButton[] PageTabs => _pageTabs ??= new[] { TabHome, TabTuning, TabLights, TabOverlay };

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
        if (page == TuningPage) RefreshGpuIfShown();   // 2.0.0-beta1.4: the GPU MODE row is read when it shows
        // fresh values right away on a sensor page (the timer would take up to 2 s)
        if (SensorsShown) Dispatcher.BeginInvoke(new Action(async () => await RefreshSensorsAsync()), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Starts the sensor and power timers only while a sensor page is on screen; stops them otherwise.</summary>
    private void UpdatePolling()
    {
        // beta1.3-ov: the overlay keeps the pollers its level needs running while the window is hidden (shared, never twice)
        bool page = SensorsShown && !_openParked;
        if (page || OvNeeds(Need.Acpi) || OvNeeds(Need.Usage)) { if (!_poll.IsEnabled) _poll.Start(); }
        else _poll.Stop();
        if ((page || OvNeeds(Need.Power)) && !_rtxSwitching) { if (!_powerTimer.IsEnabled) _powerTimer.Start(); }
        else
        {
            _powerTimer.Stop();
            // 2.0.0-beta1.4 (services review #9): nothing reads GPU power now, so the NVML session closes too
            _ = Task.Run(_power.CloseGpu);   // no-op when it isn't open
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
    /// Off goes back to the last power mode, as F1 does. 2.0.0-beta1.3: the same path as F1 (SetGModeAsync).</summary>
    private async void HomeGMode_Click(object sender, RoutedEventArgs e)
    {
        bool want = HomeGModeToggle.IsChecked == true;
        if (want == (_gmodeTarget ?? _gmodeOn)) { UpdateHomeState(); return; }
        await SetGModeAsync(want, fromKey: false);
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
        // 2.0.0-beta1.4 (Amine, bug B): no offsets shown while the RTX is off (iGPU only) or there is no NVML
        HomeGModeSub.Text = OcUnavailable ? (_gpu.Kind == GpuModeKind.IGpuOnly || _rtxSwitching ? "GPU overclock off while the RTX is off" : "No GPU overclock (NVIDIA driver missing)")
            : !oc ? "No GPU overclock kept (Tuning)"
            : !_settings.OcInGMode && !_settings.OcInPerformance ? "GPU overclock off (Tuning)"
            : !_settings.OcInGMode ? "GPU overclock in Performance only (Tuning)"
            : $"GPU overclock +{_settings.OcCore} / +{_settings.OcMem} on AC";
    }

    // beta1.3-ov: OnF4Pressed lives in MainWindow.Overlay.cs (F4 toggles the overlay off / last level)
}
