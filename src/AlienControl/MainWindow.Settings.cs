using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// Settings page inside the main window (gear in the footer). 2.0: two tabs, GENERAL · KEYS and ALERTS · ADVANCED,
/// in the same 440 x 820 card, so opening it swaps the page in place (no card swap, the height never changes).
/// Back arrow or Esc returns. Run on startup lives in the footer only; the backlight timeout moved to the Lights tab.
/// Dependent rows dim when their parent is off. The last tab is remembered for this session only.
/// </summary>
public partial class MainWindow
{
    private Grid? _setTab;   // last opened tab page (session only)

    public bool SettingsShown => SettingsPage.Visibility == Visibility.Visible;

    private void InitSettingsPage()
    {
        Sounds.Pack = _settings.ChimePack == "Classic" ? Sounds.PackKind.Classic : Sounds.PackKind.Soft;
        Toast.Style = _settings.ToastStyle == "Classic" ? Toast.ToastStyle.Classic : Toast.ToastStyle.Pill;
        ChimeSoft.IsChecked = Sounds.Pack == Sounds.PackKind.Soft;
        ChimeClassic.IsChecked = !ChimeSoft.IsChecked;
        ToastPill.IsChecked = Toast.Style == Toast.ToastStyle.Pill;
        ToastClassic.IsChecked = !ToastPill.IsChecked;
        UpdateSettingsDims();
        PreviewKeyDown += SettingsKeyDown;
    }

    private void SetSettingsShown(bool on)
    {
        var main = on ? Visibility.Collapsed : Visibility.Visible;
        MainHeader.Visibility = main;
        MainTabs.Visibility = main;
        MainBody.Visibility = main;
        StatusText.Visibility = main;
        SettingsHintText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        // the gear stays lit while the page is open (mockup)
        // 2.0.0-beta1.4 (Amine): the lit gear's fill is the lighting-colour tint (was a fixed teal #06303A)
        if (on) { SettingsButton.SetResourceReference(BorderBrushProperty, "AccentBrush"); SettingsButton.SetResourceReference(ForegroundProperty, "AccentBrush"); SettingsButton.SetResourceReference(BackgroundProperty, "AccentPillBg"); }
        else { SettingsButton.ClearValue(BorderBrushProperty); SettingsButton.ClearValue(ForegroundProperty); SettingsButton.ClearValue(BackgroundProperty); }
        UpdatePolling();   // no sensor reads behind the settings page
        if (!on) { if (IsVisible) _ = RefreshSensorsAsync(); return; }
        PresetPopup.IsOpen = false;
        CustomPopup.IsOpen = false;
        ShowTab(_setTab ?? SetPage1);
        Dispatcher.BeginInvoke(new Action(() => TabButton(_setTab ?? SetPage1).Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SetSettingsShown(!SettingsShown);

    private void SettingsBack_Click(object sender, RoutedEventArgs e) => CloseSettings();

    /// <summary>Back to the main view.</summary>
    private void CloseSettings()
    {
        if (!SettingsShown) return;
        SetSettingsShown(false);
    }

    // ---------- tabs ----------

    private Grid[] Panels => new[] { SetPage1, SetPage2 };
    private RadioButton[] Tabs => new[] { SetTab1, SetTab2 };
    private RadioButton TabButton(Grid panel) => Tabs[Array.IndexOf(Panels, panel)];

    private void ShowTab(Grid panel)
    {
        _setTab = panel;
        foreach (var p in Panels) p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
        TabButton(panel).IsChecked = true;
    }

    private void SetTab_Click(object sender, RoutedEventArgs e)
    {
        int i = Array.IndexOf(Tabs, sender as RadioButton);
        if (i >= 0) ShowTab(Panels[i]);
    }

    /// <summary>Esc = back; Left / Right on a settings tab = the other tab. On the main view, Ctrl + Tab /
    /// Ctrl + Shift + Tab move between HOME, TUNING, LIGHTS and OVERLAY.</summary>
    private void SettingsKeyDown(object sender, KeyEventArgs e)
    {
        if (GpuDialogLayer.Visibility == Visibility.Visible) return;
        if (!SettingsShown)
        {
            if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                CycleTab(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Escape) { CloseSettings(); e.Handled = true; return; }
        if (e.Key is not (Key.Left or Key.Right) || Keyboard.FocusedElement is not RadioButton rb) return;
        int i = Array.IndexOf(Tabs, rb);
        if (i < 0) return;
        int n = (i + (e.Key == Key.Right ? 1 : Tabs.Length - 1)) % Tabs.Length;
        ShowTab(Panels[n]);
        Tabs[n].Focus();
        e.Handled = true;
    }

    // ---------- dependent rows ----------

    private void UpdateSettingsDims()
    {
        Dim(ToastStyleRow, _settings.Toasts);
        Dim(ChimeRow, _settings.ToastSound);
        Dim(KbdBatteryRow, _settings.KbdTimeoutMin > 0);
        Dim(KbdFullscreenRow, _settings.KbdTimeoutMin > 0);
        bool pill = Toast.Style == Toast.ToastStyle.Pill;
        ToastsSub.Text = !_settings.Toasts ? "Off: no toasts" : pill ? "Pill toast for F1, F2 and plug / unplug" : "Top-right toast for F1, F2 and plug / unplug";
        ToastStyleSub.Text = pill ? "Top-center pill" : "Top-right card (1.2)";
    }

    private static void Dim(UIElement row, bool on)
    {
        row.Opacity = on ? 1 : 0.38;
        row.IsEnabled = on;
    }

    // ---------- chime pack ----------

    private void ChimePack_Click(object sender, RoutedEventArgs e)
    {
        var pack = sender == ChimeClassic ? Sounds.PackKind.Classic : Sounds.PackKind.Soft;
        _settings.ChimePack = pack.ToString();
        _settings.Save();
        Sounds.Pack = pack;   // frees the old pack's buffers, the next sound is the new pack
        Sounds.Play(Sounds.Kind.Balanced);
    }

    private int _previewGen;

    private async void ChimePreview_Click(object sender, RoutedEventArgs e)
    {
        int gen = ++_previewGen;
        Sounds.Play(Sounds.Kind.Balanced);
        await Task.Delay(1100);
        if (gen == _previewGen) Sounds.Play(Sounds.Kind.GModeOn);
    }

    // ---------- toast style ----------

    private void ToastStyle_Click(object sender, RoutedEventArgs e)
    {
        var style = sender == ToastClassic ? Toast.ToastStyle.Classic : Toast.ToastStyle.Pill;
        _settings.ToastStyle = style.ToString();
        _settings.Save();
        Toast.Style = style;
        UpdateSettingsDims();
    }

    private void ToastTest_Click(object sender, RoutedEventArgs e)
    {
        Toast.Show(new Toast.Info(TrayProfile.Balanced, Profiles.IconKey(TrayProfile.Balanced),
            Profiles.Label(TrayProfile.Balanced), PresetSub(TrayProfile.Balanced), Toast.Right.Pills), Motion.Enabled);
    }

    // 2.0.0-beta1.4 (Amine): the Log frame gaps switch was removed

    // ---------- 2.0.0-beta1.4 (ui review, MainWindow split): settings toggles moved here unchanged from MainWindow.xaml.cs ----------

    private void Taskbar_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowInTaskbar = TaskbarToggle.IsChecked == true;
        _settings.Save();
        ShowInTaskbar = _settings.ShowInTaskbar;
    }

    private void F3Opens_Click(object sender, RoutedEventArgs e)
    {
        _settings.F3Opens = F3OpensToggle.IsChecked == true;
        _settings.Save();
    }

    private void F4Overlay_Click(object sender, RoutedEventArgs e)
    {
        _settings.F4Overlay = F4OverlayToggle.IsChecked == true;
        bool f4On = _settings.F4Overlay;
        InputThread.Invoke(() => _macro.SetOverlayHotkey(f4On));   // beta2: Shift + F4 only while F4 drives the overlay
        _settings.Save();
    }
}
