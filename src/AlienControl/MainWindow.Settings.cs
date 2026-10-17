using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// 1.3.0-beta.2 (Amine's directive, Feature 3): the gear opens a settings PAGE inside the main window instead of the
/// 290 px popup. Same card swap as F3/F4 (slide down, swap off screen, rise). Five tabs, at most 5 rows each; every
/// tab panel stays in the layout (Hidden), so the page is as tall as the tallest tab and switching tabs never moves
/// the window. Back arrow or Esc returns. The last tab is remembered for this session only. Dependent rows dim when
/// their parent is off. Also Features 1 + 2: chime pack and toast style rows.
/// </summary>
public partial class MainWindow
{
    private Border? _setTab;   // last opened tab panel (session only)

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
        MainBody.Visibility = main;
        MainFooter.Visibility = main;
        SettingsPage.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        PresetPopup.IsOpen = false;
        CustomPopup.IsOpen = false;
        ShowTab(_setTab ?? SetGeneral);
        Dispatcher.BeginInvoke(new Action(() => TabButton(_setTab ?? SetGeneral).Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!SettingsShown) SwapCard(() => SetSettingsShown(true));
    }

    private void SettingsBack_Click(object sender, RoutedEventArgs e) => CloseSettings();

    /// <summary>Back to the main view. Instant when the window is going away anyway (hidden, minimized).</summary>
    private void CloseSettings(bool instant = false)
    {
        if (!SettingsShown) return;
        if (instant) SetSettingsShown(false);
        else SwapCard(() => SetSettingsShown(false));
    }

    // ---------- tabs ----------

    private Border[] Panels => new[] { SetGeneral, SetKeys, SetAlerts, SetBacklight, SetAdvanced };
    private RadioButton[] Tabs => new[] { SetTabGeneral, SetTabKeys, SetTabAlerts, SetTabBacklight, SetTabAdvanced };
    private RadioButton TabButton(Border panel) => Tabs[Array.IndexOf(Panels, panel)];

    private void ShowTab(Border panel)
    {
        _setTab = panel;
        foreach (var p in Panels) p.Visibility = p == panel ? Visibility.Visible : Visibility.Hidden;   // Hidden keeps the height
        TabButton(panel).IsChecked = true;
    }

    private void SetTab_Click(object sender, RoutedEventArgs e)
    {
        int i = Array.IndexOf(Tabs, sender as RadioButton);
        if (i >= 0) ShowTab(Panels[i]);
    }

    /// <summary>Esc = back; Left / Right on a tab = previous / next tab.</summary>
    private void SettingsKeyDown(object sender, KeyEventArgs e)
    {
        if (!SettingsShown || GpuDialogLayer.Visibility == Visibility.Visible) return;
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

    // ---------- Feature 1: chime pack ----------

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

    // ---------- Feature 2: toast style ----------

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
        Toast.Show(IsLegend, new Toast.Info(TrayProfile.Balanced, Profiles.IconKey(TrayProfile.Balanced),
            Profiles.Label(TrayProfile.Balanced), PresetSub(TrayProfile.Balanced), Toast.Right.Pills), Motion.Enabled);
    }
}
