using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AlienControl.Services;

namespace AlienControl;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AlienFan _fan = new();
    private readonly AlienFx _fx;
    private readonly Settings _settings = Settings.Load();
    private readonly GKeyHook _gkey;
    private int _lastPowerIndex;
    private byte[] _lastColor = { 0, 229, 255 };
    private readonly DispatcherTimer _poll = new();
    private bool _polling;
    private bool _allowClose;
    private int _fanCount = 2;
    private bool _gmodeOn;

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

        _poll.Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.PollSeconds));
        _poll.Tick += async (_, _) => await RefreshSensorsAsync();

        // Poll sensors only while the window is visible; idle in the tray.
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) { await RefreshStateAsync(); _poll.Start(); }
            else _poll.Stop();
        };

        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1");
        StartupToggle.IsChecked = Startup.IsEnabled();

        if (!_fan.IsAvailable || !_fx.IsAvailable)
            Status("Put alienfan-cli.exe and alienfx-cli.exe in the tools folder next to AlienControl.exe.");
    }

    public void AllowClose() { _allowClose = true; _gkey.Dispose(); }

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
            SetRpm(CpuFanText, CpuFanBar, cpuFan);
            SetRpm(GpuFanText, GpuFanBar, gpuFan);
        }
        finally { _polling = false; }
    }

    private static void SetTemp(TextBlock text, ProgressBar bar, TempReading? t)
    {
        text.Text = t is null ? "--" : $"{t.Celsius}°C";
        bar.Value = t?.Celsius ?? 0;
    }

    private static void SetRpm(TextBlock text, ProgressBar bar, FanReading? f)
    {
        text.Text = f is null ? "--" : $"{f.Rpm}";
        bar.Value = f?.Rpm ?? 0;
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
        Report(await _fx.SetAllAsync(_lastColor[0], _lastColor[1], _lastColor[2]));
    }

    // APIv5 keyboard effects in the last picked color: 2 = Breathing, 3 = Single-color wave.
    private async void Breathe_Click(object sender, RoutedEventArgs e) =>
        Report(await _fx.SetKeyboardEffectAsync(2, _lastColor[0], _lastColor[1], _lastColor[2]));

    private async void Wave_Click(object sender, RoutedEventArgs e) =>
        Report(await _fx.SetKeyboardEffectAsync(3, _lastColor[0], _lastColor[1], _lastColor[2]));

    private async void LightsOff_Click(object sender, RoutedEventArgs e) => Report(await _fx.OffAsync());

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

    private static int TagInt(object sender) => int.Parse((string)((FrameworkElement)sender).Tag);

    private void Report(CliResult r)
    {
        var msg = (r.Ok ? r.Output : (r.Error.Length > 0 ? r.Error : r.Output)).Trim();
        var last = msg.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        Status(last);
    }

    private void Status(string text) => StatusText.Text = text;
}
