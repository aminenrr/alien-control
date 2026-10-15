using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using AlienControl.Services;

namespace AlienControl;

public partial class MainWindow : Window
{
    private readonly AlienFan _fan = new();
    private readonly AlienFx _fx;
    private readonly Settings _settings = Settings.Load();
    private readonly GKeyHook _gkey;
    private int _lastPowerIndex;
    private byte[] _lastColor = { 0, 229, 255 };
    private readonly DispatcherTimer _indicatorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Collections.Generic.Dictionary<int, bool> _indicators = new();
    private bool _lightsManaged;   // true once the app has set a color (keyboard left firmware mode)
    private bool _indicatorBusy;
    private readonly DispatcherTimer _poll = new();
    private bool _polling;
    private bool _allowClose;
    private int _fanCount = 2;
    private bool _gmodeOn;
    private readonly SystemStats _stats = new();

    public MainWindow()
    {
        InitializeComponent();
        _fx = new AlienFx(_settings);
        _lastPowerIndex = _settings.BalancedPowerIndex;

        _gkey = new GKeyHook(_settings.GKeyVk, _settings.GKeyScan) { Enabled = _settings.GKeyEnabled };
        _gkey.Pressed += () => Dispatcher.BeginInvoke(new Action(async () => await ToggleGModeAsync()));
        _gkey.Install();
        GKeyToggle.IsChecked = _settings.GKeyEnabled;
        UpdateGKeyText();

        foreach (var id in new[] { _settings.GModeKey, _settings.TouchpadKey, _settings.SpeakerMuteKey, _settings.MicMuteKey, _settings.WinLockKey })
            _indicators[id] = false;
        if (_settings.LastColor is { Length: 3 } saved) _lastColor = saved;
        ApplyAccent(_lastColor);
        BrightnessSlider.Value = Math.Round(_settings.Brightness * 100 / 255.0);
        SelectEffectInBox();
        // Status-key watcher: in-process reads only (no CLI) unless a state changes.
        _indicatorTimer.Tick += async (_, _) => await SyncIndicatorsAsync();
        Dispatcher.BeginInvoke(new Action(async () => await StartupLightingAsync()));

        _poll.Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.PollSeconds));
        _poll.Tick += async (_, _) => await RefreshSensorsAsync();

        // Poll sensors only while the window is visible; idle in the tray.
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) { await RefreshStateAsync(); _poll.Start(); }
            else _poll.Stop();
        };

        SizeChanged += (_, _) => SnapToCorner();
        SystemParameters.StaticPropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.BeginInvoke(new Action(SnapToCorner));
        };

        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1");
        StartupToggle.IsChecked = Startup.IsEnabled();

        if (!_fan.IsAvailable || !_fx.IsAvailable)
            Status("Put alienfan-cli.exe and alienfx-cli.exe in the tools folder next to AlienControl.exe.");
    }

    // ---------- window: G-Helper style, pinned to the bottom-right corner above the taskbar ----------

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    private const uint ABM_GETSTATE = 4, ABM_GETTASKBARPOS = 5, ABS_AUTOHIDE = 1;

    /// <summary>
    /// Bottom-right corner, above the taskbar. WorkArea ignores an auto-hide taskbar (it reports the
    /// full screen), so the taskbar's real size is read from the shell and always kept clear.
    /// </summary>
    public void SnapToCorner()
    {
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        double right = wa.Right, bottom = wa.Bottom;
        try
        {
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            bool autoHide = ((uint)SHAppBarMessage(ABM_GETSTATE, ref abd).ToInt64() & ABS_AUTOHIDE) != 0;
            if (autoHide && SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
            {
                var src = PresentationSource.FromVisual(this);
                double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
                double sy = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
                double tbH = (abd.rc.Bottom - abd.rc.Top) * sy, tbW = (abd.rc.Right - abd.rc.Left) * sx;
                switch (abd.uEdge)
                {
                    case 3: bottom = SystemParameters.PrimaryScreenHeight - tbH; break; // bottom
                    case 2: right = SystemParameters.PrimaryScreenWidth - tbW; break;   // right
                }
            }
        }
        catch { /* fall back to WorkArea */ }
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : 700;
        Left = right - w - 4;
        Top = Math.Max(0, bottom - h - 4);
    }

    protected override void OnContentRendered(EventArgs e) { base.OnContentRendered(e); SnapToCorner(); }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e) { /* pinned; no dragging */ }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    public void AllowClose() { StopAnimation(); _allowClose = true; _stats.Dispose(); _gkey.Dispose(); _indicatorTimer.Stop(); }

    // ---------- status keys ----------

    private async Task StartupLightingAsync()
    {
        if (!_fx.IsAvailable) return;
        _gmodeOn = await _fan.GetGModeAsync() == true;
        await _fx.ResetHardwareDimAsync();
        ReadIndicatorStates();
        // Reapply the user's color after boot (firmware resets to its default); otherwise leave firmware lighting alone.
        if (_settings.LastColor is { Length: 3 } c)
        {
            if (_settings.LastEffect >= 100) await ApplyLightingAsync(c);
            else if (_settings.LastEffect == 0) await _fx.SetAllAsync(c[0], c[1], c[2], _indicators, _settings.Brightness);
            else await _fx.SetEffectAsync(_settings.LastEffect, c, _settings.Brightness);
            _lightsManaged = true;
        }
        _indicatorTimer.Start();
    }

    /// <summary>Reads current states into _indicators; returns the keys whose state changed.</summary>
    private System.Collections.Generic.List<int> ReadIndicatorStates()
    {
        var changed = new System.Collections.Generic.List<int>();
        void Set(int key, bool? value)
        {
            if (value is not { } v) return;
            if (!_indicators.TryGetValue(key, out var old) || old != v) changed.Add(key);
            _indicators[key] = v;
        }
        Set(_settings.GModeKey, _gmodeOn);
        Set(_settings.SpeakerMuteKey, SystemState.SpeakerMuted());
        Set(_settings.MicMuteKey, SystemState.MicMuted());
        Set(_settings.TouchpadKey, SystemState.TouchpadOff());
        return changed;
    }

    private async Task SyncIndicatorsAsync()
    {
        if (_indicatorBusy) return;
        _indicatorBusy = true;
        try
        {
            var changed = ReadIndicatorStates();
            if (!_lightsManaged || _settings.LastEffect != 0) return; // a static frame would stop the effect
            // One full frame: writing a single status key would blank the others.
            if (changed.Count > 0) await _fx.SetKeyboardAsync(_lastColor, _indicators, _settings.Brightness);
        }
        finally { _indicatorBusy = false; }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The X button hides to tray; Quit really exits.
        if (!_allowClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    // ---------- reads ----------

    private async Task RefreshStateAsync()
    {
        await RefreshSensorsAsync();

        var g = await _fan.GetGModeAsync();
        _gmodeOn = g == true;
        if (_gmodeOn) { ModeGMode.IsChecked = true; PowerHint.Text = "G-Mode"; return; }

        var raw = await _fan.GetPowerRawAsync();
        if (raw is { } value)
        {
            int idx = _settings.PowerRawByIndex.FirstOrDefault(kv => kv.Value == value).Key;
            if (idx > 0) _lastPowerIndex = idx;
            ModeQuiet.IsChecked = idx == _settings.QuietPowerIndex;
            ModeBalanced.IsChecked = idx == _settings.BalancedPowerIndex;
            ModePerformance.IsChecked = idx == _settings.PerformancePowerIndex;
            PowerHint.Text = idx == _settings.QuietPowerIndex ? "Quiet"
                : idx == _settings.BalancedPowerIndex ? "Balanced"
                : idx == _settings.PerformancePowerIndex ? "Performance"
                : $"Other mode ({value:x})";
        }
    }

    private async Task RefreshSensorsAsync()
    {
        if (_polling || !_fan.IsAvailable) return;
        _polling = true;
        try
        {
            var s = await _fan.ReadSensorsAsync();
            if (s.Error is not null) { Status(s.Error); return; }

            var cpu = s.Temps.FirstOrDefault(t => t.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));
            var gpu = s.Temps.FirstOrDefault(t => t.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase));
            SetTemp(CpuTempText, CpuTempBar, cpu);
            SetTemp(GpuTempText, GpuTempBar, gpu);

            if (s.Fans.Count > 0) _fanCount = s.Fans.Count;
            var cpuFan = s.Fans.FirstOrDefault(f => f.Type == "CPU") ?? s.Fans.ElementAtOrDefault(0);
            var gpuFan = s.Fans.FirstOrDefault(f => f.Type == "GPU") ?? s.Fans.ElementAtOrDefault(1);
            SetFanPercent(CpuFanText, CpuFanBar, cpuFan);
            SetFanPercent(GpuFanText, GpuFanBar, gpuFan);

            if (SystemStats.Ram() is { } ram) SetMem(RamText, RamTextUnit, RamBar, ram.Used, ram.Total);
            if (_stats.Vram() is { } vram) SetMem(VramText, VramTextUnit, VramBar, vram.Used, vram.Total);
        }
        finally { _polling = false; }
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

    // ---------- actions ----------

    private async void Mode_Click(object sender, RoutedEventArgs e)
    {
        CliResult r;
        if (sender == ModeGMode)
        {
            r = await _fan.SetGModeAsync(true);
            _gmodeOn = true;
            PowerHint.Text = "G-Mode";
        }
        else
        {
            if (_gmodeOn) await _fan.SetGModeAsync(false);
            int idx = sender == ModeQuiet ? _settings.QuietPowerIndex
                    : sender == ModeBalanced ? _settings.BalancedPowerIndex
                    : _settings.PerformancePowerIndex;
            r = await _fan.SetPowerAsync(idx);
            _gmodeOn = false;
            _lastPowerIndex = idx;
            PowerHint.Text = ((RadioButton)sender).Content?.ToString() ?? "";
        }
        Report(r);
        await SyncIndicatorsAsync();
    }

    /// <summary>G-key press: toggle G-Mode; turning it off returns to the last power mode.</summary>
    private async Task ToggleGModeAsync()
    {
        var current = await _fan.GetGModeAsync() ?? _gmodeOn;
        if (current)
        {
            await _fan.SetGModeAsync(false);
            Report(await _fan.SetPowerAsync(_lastPowerIndex));
            _gmodeOn = false;
        }
        else
        {
            Report(await _fan.SetGModeAsync(true));
            _gmodeOn = true;
        }
        if (IsVisible) await RefreshStateAsync();
        await SyncIndicatorsAsync();
    }

    private void GKeyToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.GKeyEnabled = _gkey.Enabled = GKeyToggle.IsChecked == true;
        _settings.Save();
        UpdateGKeyText();
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
        GKeyText.Text = $"G-key toggles G-Mode  ·  key 0x{_settings.GKeyVk:X2}" + (_settings.GKeyEnabled ? "" : " (off)");

    private async void Cpu_Click(object sender, RoutedEventArgs e) =>
        // AC level from the button; battery stays on "efficient" (3).
        Report(await _fan.SetCpuBoostAsync(TagInt(sender), 3));

    private async void Fan_Click(object sender, RoutedEventArgs e)
    {
        int boost = sender == FanMax ? _settings.MaxFanBoost : 0;
        Report(await _fan.SetFanBoostAsync(Enumerable.Repeat(boost, _fanCount).ToArray()));
        FanHint.Text = boost == 0 ? "BIOS controlled" : $"Boost {boost}%";
    }

    private async void Color_Click(object sender, RoutedEventArgs e)
    {
        _lastColor = ((string)((FrameworkElement)sender).Tag).Split(',').Select(byte.Parse).ToArray();
        await ApplyLightingAsync(_lastColor);
    }

    private async Task ApplyColorAsync(byte[] c)
    {
        ReadIndicatorStates();
        Report(await _fx.SetAllAsync(c[0], c[1], c[2], _indicators, _settings.Brightness));
        _lightsManaged = true;
        ApplyAccent(c);
        _settings.LastColor = c;
        _settings.Save();
    }

    /// <summary>Applies the saved effect (Static = per-key frame with status keys) in the given color.</summary>
    private async Task ApplyLightingAsync(byte[] c)
    {
        StopAnimation();
        if (_settings.LastEffect >= 100 && !c.All(v => v == 0))
        {
            Report(await _fx.SetChassisCycleAsync(_settings.LastEffect == 101));
            StartAnimation(_settings.LastEffect == 101);
            _lightsManaged = true;
            ApplyAccent(c);
            _settings.LastColor = c;
            _settings.Save();
            return;
        }
        if (_settings.LastEffect == 0 || c.All(v => v == 0)) { await ApplyColorAsync(c); return; }
        Report(await _fx.SetEffectAsync(_settings.LastEffect, c, _settings.Brightness));
        _lightsManaged = true;
        ApplyAccent(c);
        _settings.LastColor = c;
        _settings.Save();
    }

    private bool _effectBoxReady;

    // ---------- software effects (APIv5 has no spectrum, so the app animates the keys itself) ----------

    private System.Threading.CancellationTokenSource? _anim;

    private void StopAnimation()
    {
        _anim?.Cancel();
        _anim = null;
    }

    /// <summary>
    /// Spectrum: all keys cycle through the hues together. Rainbow wave: hues spread across the keys
    /// and scroll. Key IDs roughly follow rows, so the wave runs by ID until columns are mapped.
    /// Status keys stay white when on, because every frame includes them.
    /// </summary>
    private void StartAnimation(bool wave)
    {
        var cts = new System.Threading.CancellationTokenSource();
        _anim = cts;
        var token = cts.Token;
        Task.Run(async () =>
        {
            double t = 0;
            while (!token.IsCancellationRequested)
            {
                var ind = new System.Collections.Generic.Dictionary<int, bool>(_indicators);
                double hueBase = t;
                _fx.SendFrame(i => HsvToRgb(wave ? hueBase + KeyColumn(i) * 15 : hueBase, 1), ind, _settings.Brightness);
                t = (t + (wave ? 6 : 3)) % 360;
                try { await Task.Delay(50, token); } catch { break; }
            }
        }, token);
    }

    /// <summary>Approximate column of a key: IDs run left to right in rows of about 23.</summary>
    private static int KeyColumn(int id) => id % 23;

    private async void Effect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_effectBoxReady || EffectBox.SelectedItem is not ComboBoxItem item) return;
        _settings.LastEffect = int.Parse((string)item.Tag);
        if (_lastColor.All(v => v == 0)) _lastColor = new byte[] { 0, 229, 255 };
        await ApplyLightingAsync(_lastColor);
    }

    private void SelectEffectInBox()
    {
        foreach (ComboBoxItem it in EffectBox.Items)
            if ((string)it.Tag == _settings.LastEffect.ToString()) { EffectBox.SelectedItem = it; break; }
        if (EffectBox.SelectedItem is null) EffectBox.SelectedIndex = 0;
        _effectBoxReady = true;
    }

    // ---------- custom color (hue + saturation at full value; brightness is the slider) ----------

    private void CustomSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (!_lastColor.All(v => v == 0)) _custom = (byte[])_lastColor.Clone();
        UpdateCustomPreview();
        CustomPopup.IsOpen = true;
    }

    private bool _syncingRgb;
    private byte[] _custom = { 0, 229, 255 };

    // Sliders -> color and boxes.
    private void CustomColor_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingRgb || HueSlider is null || SatSlider is null) return;
        _custom = HsvToRgb(HueSlider.Value, SatSlider.Value / 100.0);
        UpdateCustomPreview();
    }

    // Typed R, G, B (0..255) -> color.
    private void Rgb_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingRgb || RBox is null || GBox is null || BBox is null) return;
        if (byte.TryParse(RBox.Text, out var r) && byte.TryParse(GBox.Text, out var g) && byte.TryParse(BBox.Text, out var b))
        {
            _custom = new[] { r, g, b };
            if (CustomPreview is not null) CustomPreview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        }
    }

    private void UpdateCustomPreview()
    {
        if (CustomPreview is null || RBox is null) return;
        var c = _custom;
        CustomPreview.Background = new SolidColorBrush(Color.FromRgb(c[0], c[1], c[2]));
        _syncingRgb = true;
        RBox.Text = c[0].ToString(); GBox.Text = c[1].ToString(); BBox.Text = c[2].ToString();
        _syncingRgb = false;
    }

    private async void CustomApply_Click(object sender, RoutedEventArgs e)
    {
        CustomPopup.IsOpen = false;
        _lastColor = (byte[])_custom.Clone();
        await ApplyLightingAsync(_lastColor);
    }

    private static byte[] HsvToRgb(double h, double s)
    {
        h = (h % 360 + 360) % 360;
        double c = s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = 1 - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return new[] { (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255) };
    }

    private async void LightsOff_Click(object sender, RoutedEventArgs e)
    {
        StopAnimation();
        _lastColor = new byte[] { 0, 0, 0 };
        await ApplyColorAsync(_lastColor);
    }

    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessHint is not null) BrightnessHint.Text = $"Brightness {(int)e.NewValue}%";
    }

    private async void Brightness_Commit(object sender, MouseButtonEventArgs e)
    {
        int level = (int)Math.Round(BrightnessSlider.Value * 255 / 100);
        _settings.Brightness = level;
        _settings.Save();
        // Software brightness: repaint with scaled colors (only once the app manages the lights).
        if (_lightsManaged) await ApplyLightingAsync(_lastColor);
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        bool want = StartupToggle.IsChecked == true;
        if (!Startup.SetEnabled(want))
        {
            StartupToggle.IsChecked = !want;
            Status("Couldn't change the startup task.");
        }
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).Quit();

    // ---------- helpers ----------

    /// <summary>App accent follows the keyboard / ring color. Black (lights off) keeps the default cyan.</summary>
    private static void ApplyAccent(byte[] c)
    {
        var col = (c[0] + c[1] + c[2]) < 60 ? Color.FromRgb(0, 229, 255) : Color.FromRgb(c[0], c[1], c[2]);
        var res = Application.Current.Resources;
        var lighter = Color.FromRgb((byte)((col.R + 255) / 2), (byte)((col.G + 255) / 2), (byte)((col.B + 255) / 2));
        var darker = Color.FromRgb((byte)(col.R * 0.55), (byte)(col.G * 0.55), (byte)(col.B * 0.55));
        res["AccentBrush"] = Freeze(new SolidColorBrush(col));
        var grad = new LinearGradientBrush(col, lighter == col ? darker : (col.R + col.G + col.B > 600 ? darker : lighter), 0);
        res["AccentGradient"] = Freeze(grad);
        var chk = new LinearGradientBrush(Color.FromArgb(0x33, col.R, col.G, col.B), Color.FromArgb(0x10, col.R, col.G, col.B), 90);
        res["CyanChecked"] = Freeze(chk);
        try { Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(col, Wpf.Ui.Appearance.ApplicationTheme.Dark); } catch { }
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static int TagInt(object sender) => int.Parse((string)((FrameworkElement)sender).Tag);

    private void Report(CliResult r)
    {
        var msg = (r.Ok ? r.Output : (r.Error.Length > 0 ? r.Error : r.Output)).Trim();
        var last = msg.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        Status(last);
    }

    private void Status(string text) => StatusText.Text = text;
}
