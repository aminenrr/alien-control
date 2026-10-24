using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// v1.1 beta.5 (Amine 2026-10-16): a header button shows the active preset's icon in its color. 2.0: it is hidden on
/// Home (the presets are on the page); a click cycles like F2 (silent, like every window click), a right click (or a
/// click while G-Mode is on) opens the list of presets (Battery, Quiet, Balanced, Performance, Custom), each with its
/// icon and color, the active one marked. Picking one runs the same path as the tray menu.
/// </summary>
public partial class MainWindow
{
    private static readonly TrayProfile[] PresetOrder =
        { TrayProfile.Battery, TrayProfile.Quiet, TrayProfile.Balanced, TrayProfile.Performance, TrayProfile.Custom };
    private DateTime _presetClosedUtc;

    private bool _presetCycleBusy;

    /// <summary>2.0: click = next preset, the F2 order (Quiet, Balanced, Performance; Battery on battery). In G-Mode
    /// (F1 owns it) or with the list open, the list opens instead.</summary>
    private async void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if ((DateTime.UtcNow - _presetClosedUtc).TotalMilliseconds < 250) return;   // this click just closed the list
        if (_gmodeOn) { OpenPresetList(); return; }
        if (_presetCycleBusy) return;
        _presetCycleBusy = true;
        try
        {
            var next = NextPreset(CurrentProfile);
            if (next == TrayProfile.Battery && BatteryLocked) next = TrayProfile.Quiet;
            await ApplyProfileAsync(next);
        }
        finally { _presetCycleBusy = false; }
    }

    private void PresetButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenPresetList();
    }

    private void OpenPresetList()
    {
        // StaysOpen=False closes the popup on the mouse-down of this very click; don't reopen it right away.
        if ((DateTime.UtcNow - _presetClosedUtc).TotalMilliseconds < 250) return;
        CloseSettings();
        BuildPresetList();
        PresetPopup.HorizontalOffset = PresetButton.ActualWidth - PresetPanel.Width;   // right edge under the button
        PresetPopup.VerticalOffset = 2;
        PresetPopup.IsOpen = true;
    }

    private static SolidColorBrush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private void PresetPopup_Closed(object? sender, EventArgs e) => _presetClosedUtc = DateTime.UtcNow;

    private void BuildPresetList()
    {
        PresetList.Children.Clear();
        var shown = ShownProfile;
        var hover = TryFindResource("HoverFxBrush") as Brush ?? Brushes.Transparent;
        var textStyle = TryFindResource("FooterText") as Style;
        foreach (var p in PresetOrder)
        {
            var t = Profiles.Tint(p);
            var tint = new SolidColorBrush(Color.FromRgb(t.R, t.G, t.B)); tint.Freeze();
            bool active = p == shown;
            bool locked = p == TrayProfile.Battery && BatteryLocked;

            var row = new Grid { Margin = new Thickness(0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new ContentControl
            {
                Content = TryFindResource(Profiles.IconKey(p)), Width = 16, Height = 16, Foreground = tint,
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false,
            };
            TextElement.SetForeground(icon, tint);
            row.Children.Add(icon);

            var label = Profiles.Label(p);
            var text = new TextBlock
            {
                Text = label.ToUpperInvariant(), Style = textStyle, VerticalAlignment = VerticalAlignment.Center,
            };
            if (active) { text.Foreground = tint; text.FontWeight = FontWeights.SemiBold; }
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            if (active)
            {
                var dot = new Border
                {
                    Width = 6, Height = 6, CornerRadius = new CornerRadius(1), Background = tint,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0),
                };
                Grid.SetColumn(dot, 2);
                row.Children.Add(dot);
            }

            var item = new Border
            {
                Child = row, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(3),
                Background = active ? FrozenBrush(Color.FromArgb(0x1C, t.R, t.G, t.B)) : Brushes.Transparent,   // 2.0.0-beta1.4: frozen
                Cursor = locked ? Cursors.Arrow : Cursors.Hand, Opacity = locked ? 0.4 : 1,
                ToolTip = locked ? "Battery comes on by itself when you unplug (Auto preset)" : Profiles.Describe(p),
            };
            if (!locked)
            {
                var rest = item.Background;
                item.MouseEnter += (_, _) => { if (!active) item.Background = hover; };
                item.MouseLeave += (_, _) => item.Background = rest;
                item.MouseLeftButtonUp += async (_, _) =>
                {
                    PresetPopup.IsOpen = false;
                    if (p != shown || p != CurrentProfile) await ApplyProfileAsync(p);
                };
            }
            PresetList.Children.Add(item);
        }
    }

    // ---------- 2.0.0-beta1.4 (ui review, MainWindow split): moved here unchanged from MainWindow.xaml.cs ----------

    // ---------- tray profiles (power mode + refresh rate; never GPU mode, never Ultimate) ----------

    public async Task ApplyProfileAsync(TrayProfile p)
    {
        await _cmdGate.WaitAsync();   // 2.0.0-beta1.3 (ui review #4): one command at a time
        try { await ApplyProfileCoreAsync(p); }
        finally { _cmdGate.Release(); }
    }

    private async Task ApplyProfileCoreAsync(TrayProfile p)
    {
        SetProfile(p);
        if (p == TrayProfile.Custom) return;
        if (Profiles.PowerIndex(p, _settings) is { } idx)
        {
            // 2.0.0-beta1.4 (ui review #8): _gmodeOn is trusted (every read and change keeps it in sync); this used to
            // spawn a getgmode alienfan-cli run on almost every preset change (whenever G-Mode was off)
            if (_gmodeOn) { await _fan.SetGModeAsync(false); _gmodeOn = false; }
            Report(await _fan.SetPowerAsync(idx));
            _lastPowerIndex = idx;
        }
        if (Profiles.CpuBoost(p) is { } boost)
        {
            Report(await _fan.SetCpuBoostAsync(boost, boost));   // v0.8.9: same level on AC and battery
            if (await CpuFreq.ApplyAsync(boost, boost) is { } err) Status(err);
            ShowCpuBoost(boost);
        }
        if (Profiles.RefreshHz(p) is { } hz)
        {
            // v0.9.12: a real rate change blanks the screen ~1 s; the toast waits it out (unplug toast was lost)
            if (DisplayRefresh.Read() is not { } now || !DisplayRefresh.IsActive(now, hz)) _rateChangedUtc = DateTime.UtcNow;
            var err = await Task.Run(() => DisplayRefresh.Set(hz));
            if (err is not null) Status(err);
        }
        if (IsVisible) { RefreshDisplayRate(); await RefreshStateAsync(); }
        await SyncOcAsync();
        await SyncIndicatorsAsync();
    }

    private DateTime _rateChangedUtc = DateTime.MinValue;

    private void ShowCpuBoost(int level)
    {
        CpuOff.IsChecked = level == 0;
        CpuOn.IsChecked = level == 1;
        CpuAggressive.IsChecked = level == 2;
        CpuEfficient.IsChecked = level == 3;
    }

    private TrayProfile? _raisedProfile;

    private void SetProfile(TrayProfile p)
    {
        if (p != TrayProfile.GMode && _settings.Profile != p.ToString())
        {
            bool perfEdge = p == TrayProfile.Performance || _settings.Profile == nameof(TrayProfile.Performance);
            _settings.Profile = p.ToString();
            _settings.Save();
            // 1.3.4: entering or leaving Performance moves the OC when the Performance toggle is on
            if (perfEdge && _settings.OcInPerformance) Dispatcher.BeginInvoke(new Action(async () => await SyncOcAsync()));
        }
        UpdateProfileChip(p);
        // 2.0.0-beta1.4 (ui review #10): raised only when the shown profile changes (App re-tints the tray icon pixel
        // by pixel and re-renders the menu icons), not after every Mode, Refresh or G-Mode click
        if (_raisedProfile != p) { _raisedProfile = p; ProfileChanged?.Invoke(p); }
    }

    // ---------- v0.9.10 F2 preset key ----------

    /// <summary>F2 press: Quiet > Balanced > Performance > Quiet (v0.9.11: on battery Performance > Battery > Quiet).
    /// Battery/Custom start at Quiet. G-Mode: F1 owns it, do nothing.</summary>
    private async Task CyclePresetAsync()
    {
        if (!_settings.F2Presets || _gmodeOn || _f2Busy) return;   // 1.2.0-beta.5: presses during a switch don't stack
        _f2Busy = true;
        try { await CyclePresetCoreAsync(); }
        finally { _f2Busy = false; }
    }

    private bool _f2Busy;

    private async Task CyclePresetCoreAsync()
    {
        var cur = CurrentProfile;
        var next = NextPreset(cur);
        // v1.0.0-rc1: toast first, then apply (no ~1 s wait). Into / out of Battery changes the refresh rate,
        // which blanks the screen, so those still toast after the switch (Notify waits for the screen).
        bool rate = cur == TrayProfile.Battery || next == TrayProfile.Battery;
        // 1.3.4 (Amine): the Performance toast says whether the GPU OC is on (Performance tint) or off
        var (oc, ocLit) = next == TrayProfile.Performance ? OcToastTag(_settings.OcInPerformance) : (null, false);
        void Show() => Notify(new Toast.Info(next, Profiles.IconKey(next), Profiles.Label(next), PresetSub(next),
            next == TrayProfile.Battery ? Toast.Right.Chip : Toast.Right.Pills, Oc: oc, OcLit: ocLit), next switch
        {
            TrayProfile.Quiet => Sounds.Kind.Quiet, TrayProfile.Performance => Sounds.Kind.Performance,
            TrayProfile.Battery => Sounds.Kind.Unplug, _ => Sounds.Kind.Balanced,
        });
        if (!rate) { Show(); await Task.Delay(150); }   // 1.2.0-beta.3: let the chime's attack play before the hardware switch
        await ApplyProfileAsync(next);
        if (rate) Show();
    }

    private static TrayProfile NextPreset(TrayProfile p) => Profiles.NextPreset(p, OnAc);

    private async void F2Presets_Click(object sender, RoutedEventArgs e)
    {
        _settings.F2Presets = F2PresetsToggle.IsChecked == true;
        _settings.Save();
        await SyncIndicatorsAsync();
    }

    private TrayProfile? _chipProfile;

    /// <summary>Header chip: dot + profile name, tinted like the tray icon. Read-only; event driven (no timer).</summary>
    private void UpdateProfileChip(TrayProfile p)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => UpdateProfileChip(p))); return; }
        var t = Profiles.Tint(p);
        var col = Color.FromRgb(t.R, t.G, t.B);
        SolidColorBrush B(byte a) { var b = new SolidColorBrush(Color.FromArgb(a, col.R, col.G, col.B)); b.Freeze(); return b; }
        var label = Profiles.Label(p);
        bool changed = _chipProfile is { } prev && prev != p;
        _chipProfile = p;
        if (changed && Motion.Enabled && PresetButton.IsVisible)
            Motion.Play(null, (PresetButton, OpacityProperty, Motion.Tween(0.2, 1, 250, Motion.Quad)));
        PresetButton.Content = TryFindResource(Profiles.IconKey(p));
        PresetButton.Foreground = B(255);
        PresetButton.ToolTip = p == TrayProfile.GMode
            ? "G-Mode is on (F1). Click for the preset list"
            : $"Preset: {label} ({Profiles.Describe(p)}). Click: next preset, like F2. Right click: the list";
        // 2.0.0-beta1.4 (build review #4): an icon-only button, so Narrator gets a name (a ToolTip is only help text)
        System.Windows.Automation.AutomationProperties.SetName(PresetButton, $"Preset {label}");
        if (PresetPopup.IsOpen) BuildPresetList();
        UpdateHomeState();   // 2.0: Home preset tiles, G-Mode switch and F2 hint follow every change
    }

    /// <summary>After a manual change: the profile that matches mode + refresh rate, otherwise Custom.</summary>
    private void UpdateProfileFromState()
    {
        if (_gmodeOn) { SetProfile(TrayProfile.GMode); return; }   // v0.9.8: was Custom
        var match = TrayProfile.Custom;
        {
            var rate = DisplayRefresh.Read();
            foreach (TrayProfile p in new[] { TrayProfile.Battery, TrayProfile.Quiet, TrayProfile.Balanced, TrayProfile.Performance })
                if (Profiles.PowerIndex(p, _settings) == _lastPowerIndex && rate is not null && DisplayRefresh.IsActive(rate, Profiles.RefreshHz(p)!.Value))
                { match = p; break; }
        }
        SetProfile(match);
    }
}
