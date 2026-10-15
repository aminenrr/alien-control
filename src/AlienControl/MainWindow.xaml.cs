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

    public void AllowClose() { _allowClose = true; _stats.Dispose(); _gkey.Dispose(); _indicatorTimer.Stop(); }

    // ---------- status keys ----------

    private async Task StartupLightingAsync()
    {
        if (!_fx.IsAvailable) return;
        _gmodeOn = await _fan.GetGModeAsync() == true;
        ReadIndicatorStates();
        // Reapply the user's color after boot (firmware resets to its default); otherwise leave firmware lighting alone.
        if (_settings.LastColor is { Length: 3 } c)
        {
            await _fx.SetAllAsync(c[0], c[1], c[2], _indicators);
            await _fx.SetIndicatorsAsync(_indicators, c);
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
            if (!_lightsManaged) return;
            foreach (var key in changed)
            {
                var c = _indicators[key] ? _settings.IndicatorOnColor : _lastColor;
                await _fx.SetKeyAsync(key, c[0], c[1], c[2]);
            }
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
        await ApplyColorAsync(_lastColor);
    }

    private async Task ApplyColorAsync(byte[] c)
    {
        ReadIndicatorStates();
        Report(await _fx.SetAllAsync(c[0], c[1], c[2], _indicators));
        // The big batch leaves the five status keys dark; a separate command lights them (verified 2026-10-15).
        await _fx.SetIndicatorsAsync(_indicators, c);
        _lightsManaged = true;
        ApplyAccent(c);
        _settings.LastColor = c;
        _settings.Save();
    }

    // APIv5 keyboard effects in the last picked color: 2 = Breathing, 3 = Single-color wave.
    private async void Breathe_Click(object sender, RoutedEventArgs e) =>
        Report(await _fx.SetKeyboardEffectAsync(2, _lastColor[0], _lastColor[1], _lastColor[2]));

    private async void Wave_Click(object sender, RoutedEventArgs e) =>
        Report(await _fx.SetKeyboardEffectAsync(3, _lastColor[0], _lastColor[1], _lastColor[2]));

    private async void LightsOff_Click(object sender, RoutedEventArgs e)
    {
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
        Report(await _fx.SetBrightnessAsync(level));
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
