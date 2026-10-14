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
    private readonly AlienFx _fx = new();
    private readonly Settings _settings = Settings.Load();
    private readonly DispatcherTimer _poll = new();
    private bool _polling;
    private bool _allowClose;
    private int _fanCount = 2;
    private bool _gmodeOn;

    public MainWindow()
    {
        InitializeComponent();

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

    public void AllowClose() => _allowClose = true;

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

        var p = await _fan.GetPowerAsync();
        if (p is { } power)
        {
            PowerHint.Text = power.Name;
            ModeQuiet.IsChecked = power.Index == _settings.QuietPowerIndex;
            ModeBalanced.IsChecked = power.Index == _settings.BalancedPowerIndex;
            ModePerformance.IsChecked = power.Index == _settings.PerformancePowerIndex;
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
            PowerHint.Text = ((RadioButton)sender).Content?.ToString() ?? "";
        }
        Report(r);
    }

    private async void Gpu_Click(object sender, RoutedEventArgs e) =>
        Report(await _fan.SetGpuLimitAsync(TagInt(sender)));

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
        var rgb = ((string)((FrameworkElement)sender).Tag).Split(',').Select(byte.Parse).ToArray();
        Report(await _fx.SetAllAsync(rgb[0], rgb[1], rgb[2]));
    }

    private async void Spectrum_Click(object sender, RoutedEventArgs e) =>
        Report(await _fx.SetGlobalEffectAsync(_settings.LightDevice, _settings.SpectrumEffectType));

    private async void LightsOff_Click(object sender, RoutedEventArgs e) => Report(await _fx.OffAsync());

    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessHint is not null) BrightnessHint.Text = $"Brightness {(int)e.NewValue}%";
    }

    private async void Brightness_Commit(object sender, MouseButtonEventArgs e)
    {
        int level = (int)Math.Round(BrightnessSlider.Value * 255 / 100);
        Report(await _fx.SetBrightnessAsync(_settings.LightDevice, level));
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
