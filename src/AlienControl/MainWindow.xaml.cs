using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private readonly DispatcherTimer _powerTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _polling;
    private bool _allowClose;
    private int _fanCount = 2;
    private bool _gmodeOn;
    private readonly SystemStats _stats = new();
    private readonly PowerMeter _power = new();

    /// <summary>Raised when the tray profile changes (App tints the tray icon).</summary>
    public event Action<TrayProfile>? ProfileChanged;
    public TrayProfile CurrentProfile =>
        Enum.TryParse<TrayProfile>(_settings.Profile, out var p) ? p : TrayProfile.Custom;

    private bool IsLegend => _settings.Theme != "Fluent";

    public MainWindow()
    {
        // Only the active theme dictionary is loaded; it must be there before the XAML resolves its keys.
        App.ApplyTheme(_settings.Theme);
        Motion.Enabled = !_settings.ReduceMotion;   // v0.8.2: Reduce motion = no animations at all
        InitializeComponent();
        ReduceMotionToggle.IsChecked = _settings.ReduceMotion;
        ThemeLegend.IsChecked = IsLegend;
        ThemeFluent.IsChecked = !IsLegend;
        _fx = new AlienFx(_settings);
        _lastPowerIndex = _settings.BalancedPowerIndex;

        // G-key hook: always on since v0.8 (no off switch); "Detect key" lives in the gear menu.
        _gkey = new GKeyHook(_settings.GKeyVk, _settings.GKeyScan);
        _gkey.Pressed += () => Dispatcher.BeginInvoke(new Action(async () => await ToggleGModeAsync()));
        _gkey.Install();
        UpdateGKeyText();
        SetHint(FanHint, "BIOS controlled", "BIOS");
        SetHint(GpuHint, "Optimus · RTX 3070 Ti", "OPTIMUS · 3070 Ti");

        foreach (var id in new[] { _settings.GModeKey, _settings.TouchpadKey, _settings.SpeakerMuteKey, _settings.MicMuteKey, _settings.WinLockKey })
            _indicators[id] = false;
        if (_settings.LastColor is { Length: 3 } saved) _lastColor = saved;
        ApplyAccent(_lastColor);
        SelectSwatch(_lastColor);
        BrightnessSlider.Value = Math.Round(_settings.Brightness * 100 / 255.0);
        UpdateBrightnessText((int)BrightnessSlider.Value);
        SelectEffectInBox();
        // Status-key watcher: in-process reads only (no CLI) unless a state changes.
        _indicatorTimer.Tick += async (_, _) => await SyncIndicatorsAsync();
        Dispatcher.BeginInvoke(new Action(async () => await StartupLightingAsync()));

        _poll.Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.PollSeconds));
        _poll.Tick += async (_, _) => await RefreshSensorsAsync();
        _powerTimer.Tick += async (_, _) => await RefreshPowerAsync();
        _power.RestW = _settings.RestOfSystemW;

        // Poll sensors only while the window is visible; idle in the tray.
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible)
            {
                PlayOpen(fromCurrent: false);   // v0.8.6 Quick Settings style slide (instant with Reduce motion)
                int gen = _showGen;
                UpdateProfileChip(CurrentProfile);
                // v0.8.6: the hardware reads wait until the slide has played, so they can't stall its frames
                // (v0.8.5 log: a 160 ms open took 1004 ms because this work ran on the UI thread first).
                if (Motion.Enabled) await Task.Delay(OpenMs + 40);
                if (!IsVisible || gen != _showGen) return;
                RefreshDisplayRate();          // read once on show, no timer
                _ = RefreshGpuHintAsync();
                await RefreshStateAsync();
                if (!IsVisible || gen != _showGen) return;
                _poll.Start();
                _powerTimer.Start();
            }
            else
            {
                _poll.Stop(); _powerTimer.Stop(); CloseSettings(instant: true);
                // Keep the learned rest-of-laptop power for the AC estimate (saved only when it moved).
                if (Math.Abs(_settings.RestOfSystemW - _power.RestW) >= 1) { _settings.RestOfSystemW = Math.Round(_power.RestW, 1); _settings.Save(); }
            }
        };

        // v0.8.2 gear flyout (StaysOpen=True so it can animate out): close on outside click / deactivate / minimize.
        PreviewMouseDown += (_, e) =>
        {
            // Clicks inside the flyout also route here (Popup is in the window's logical tree), so skip those.
            var src = e.OriginalSource as DependencyObject;
            if (SettingsPopup.IsOpen && !_settingsPinned && !IsWithin(src, SettingsButton) && !IsWithin(src, SettingsPanel))
                CloseSettings();
        };
        Deactivated += (_, _) =>
        {
            if (SettingsPopup.IsOpen && !_settingsPinned && !SettingsPanel.IsMouseOver) CloseSettings();
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) CloseSettings(instant: true); };

        SizeChanged += (_, _) => SnapToCorner();
        SystemParameters.StaticPropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.BeginInvoke(new Action(SnapToCorner));
        };

        UpdateProfileChip(CurrentProfile);
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
    private void Close_Click(object sender, RoutedEventArgs e) => HideToTray();

    // ---------- v0.8.6 window open / hide: Windows 11 Quick Settings style slide ----------
    // The card slides up from the bottom edge (ClipHost clips it, so it rises out of the edge like the Win11 flyout)
    // and slides back down past the edge on hide. TranslateTransform.Y only, one-shot, 60 fps, nothing while hidden.

    private const int OpenMs = 250, HideMs = 170;
    private int _showGen;     // bumps on every show/hide so a stale hide never fires after a re-show
    private bool _hiding;
    private bool _openQueued;

    /// <summary>Distance that puts the card fully below the clip edge.</summary>
    private double SlideDistance => (ClipHost.ActualHeight > 0 ? ClipHost.ActualHeight : 800) + 2;

    /// <summary>fromCurrent: resume from a hide that was interrupted (no jump). Otherwise the card is parked below
    /// the edge and the slide starts on the first rendered frame, after Show() has done its layout work.</summary>
    private void PlayOpen(bool fromCurrent)
    {
        _showGen++;
        _hiding = false;
        if (!Motion.Enabled) { ResetCard(); return; }
        var shift = Motion.Translate(RootCard);
        Motion.Set(RootCard, OpacityProperty, 1);
        if (fromCurrent) { StartSlideIn(_showGen); return; }
        Motion.Set(shift, TranslateTransform.YProperty, SlideDistance);
        if (_openQueued) return;
        _openQueued = true;
        int gen = _showGen;
        EventHandler? onFrame = null;
        onFrame = (_, _) =>
        {
            CompositionTarget.Rendering -= onFrame;   // one-shot: never keeps a per-frame hook alive
            _openQueued = false;
            if (gen == _showGen && !_hiding && IsVisible) StartSlideIn(gen);
        };
        CompositionTarget.Rendering += onFrame;
    }

    private void StartSlideIn(int gen)
    {
        var shift = Motion.Translate(RootCard);
        // On completion drop the transform, so the card rests at exact Y 0 (crisp ClearType text).
        Motion.Play(() => { if (gen == _showGen && !_hiding) ResetCard(); },
            (shift, TranslateTransform.YProperty, Motion.Spline(null, 0, OpenMs, Motion.Decelerate)));
    }

    /// <summary>App.ShowMain calls this before Show(): a show during the hide animation cancels the hide.</summary>
    public void PrepareShow()
    {
        if (_hiding) PlayOpen(fromCurrent: true);
    }

    /// <summary>Slide down past the edge over 170 ms, THEN Hide(). Falls back to an instant Hide() whenever the
    /// animation can't run (Reduce motion, minimized, a second request, an exception) and, as a safety net,
    /// if the storyboard hasn't completed 400 ms later (one-shot check, nothing repeats).</summary>
    public void HideToTray()
    {
        CloseSettings(instant: true);
        if (!IsVisible) return;
        if (!Motion.Enabled || WindowState == WindowState.Minimized || _hiding) { HideNow(); return; }
        _hiding = true;
        int gen = ++_showGen;
        try
        {
            var shift = Motion.Translate(RootCard);
            Motion.Play(() => { if (gen == _showGen && _hiding) HideNow(); },
                (shift, TranslateTransform.YProperty, Motion.Spline(null, SlideDistance, HideMs, Motion.Accelerate)));
        }
        catch { HideNow(); return; }
        _ = HideSafetyNetAsync(gen);
    }

    private async Task HideSafetyNetAsync(int gen)
    {
        await Task.Delay(400);
        if (gen == _showGen && _hiding) HideNow();
    }

    private void HideNow()
    {
        _hiding = false;
        _showGen++;
        Hide();
        ResetCard();   // back to the rest position while hidden; PlayOpen parks it below the edge again on show
    }

    private void ResetCard()
    {
        Motion.Set(RootCard, OpacityProperty, 1);
        Motion.Set(Motion.Translate(RootCard), TranslateTransform.YProperty, 0);
        RootCard.RenderTransform = Transform.Identity;   // no transform at rest: text snaps to device pixels
    }

    public void AllowClose() { StopAnimation(); _allowClose = true; _stats.Dispose(); _power.Dispose(); _gkey.Dispose(); _indicatorTimer.Stop(); }

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
        if (!_allowClose) { e.Cancel = true; HideToTray(); }
        base.OnClosing(e);
    }

    // ---------- reads ----------

    private async Task RefreshStateAsync()
    {
        await RefreshSensorsAsync();

        var g = await _fan.GetGModeAsync();
        _gmodeOn = g == true;
        if (_gmodeOn) { ModeGMode.IsChecked = true; SetHint(PowerHint, "G-Mode"); return; }

        var raw = await _fan.GetPowerRawAsync();
        if (raw is { } value)
        {
            int idx = _settings.PowerRawByIndex.FirstOrDefault(kv => kv.Value == value).Key;
            if (idx > 0) _lastPowerIndex = idx;
            ModeBattery.IsChecked = idx == _settings.BatteryPowerIndex;
            ModeQuiet.IsChecked = idx == _settings.QuietPowerIndex;
            ModeBalanced.IsChecked = idx == _settings.BalancedPowerIndex;
            ModePerformance.IsChecked = idx == _settings.PerformancePowerIndex;
            SetHint(PowerHint, idx == _settings.BatteryPowerIndex ? "Battery"
                : idx == _settings.QuietPowerIndex ? "Quiet"
                : idx == _settings.BalancedPowerIndex ? "Balanced"
                : idx == _settings.PerformancePowerIndex ? "Performance"
                : $"Other mode ({value:x})");
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

    // v0.8.5: power has its own 1 s timer (perf counter + NVML are cheap; the 2 s sensor poll spawns alienfan-cli).
    private bool _powerBusy;
    private async Task RefreshPowerAsync()
    {
        if (_powerBusy) return;
        _powerBusy = true;
        try { ShowPower(await Task.Run(_power.Sample)); }
        catch { }
        finally { _powerBusy = false; }
    }

    /// <summary>One power line; parts without a source are hidden, the line hides when none answers.</summary>
    private void ShowPower(PowerMeter.Reading r)
    {
        PwrCpu.Visibility = r.CpuW is null ? Visibility.Collapsed : Visibility.Visible;
        PwrGpu.Visibility = r.GpuW is null ? Visibility.Collapsed : Visibility.Visible;
        PwrTotal.Visibility = r.TotalW is null ? Visibility.Collapsed : Visibility.Visible;
        PwrCpuValue.Text = $"{r.CpuW} W";
        PwrGpuValue.Text = $"{r.GpuW} W";
        PwrTotalValue.Text = r.TotalEstimated ? $"~{r.TotalW} W" : $"{r.TotalW} W";
        PwrTotal.ToolTip = r.TotalEstimated
            ? "Estimate on AC: CPU + GPU + rest of the laptop (learned on battery)"
            : "Measured: battery discharge rate";
        PwrDot1.Visibility = r.CpuW is not null && (r.GpuW is not null || r.TotalW is not null) ? Visibility.Visible : Visibility.Collapsed;
        PwrDot2.Visibility = r.GpuW is not null && r.TotalW is not null ? Visibility.Visible : Visibility.Collapsed;
        PwrRow.Visibility = r.CpuW is null && r.GpuW is null && r.TotalW is null ? Visibility.Collapsed : Visibility.Visible;
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
            SetHint(PowerHint, "G-Mode");
        }
        else
        {
            if (_gmodeOn) await _fan.SetGModeAsync(false);
            int idx = sender == ModeBattery ? _settings.BatteryPowerIndex
                    : sender == ModeQuiet ? _settings.QuietPowerIndex
                    : sender == ModeBalanced ? _settings.BalancedPowerIndex
                    : _settings.PerformancePowerIndex;
            r = await _fan.SetPowerAsync(idx);
            _gmodeOn = false;
            _lastPowerIndex = idx;
            SetHint(PowerHint, ((RadioButton)sender).Content?.ToString() ?? "");
        }
        Report(r);
        UpdateProfileFromState();
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
        UpdateProfileFromState();
        await SyncIndicatorsAsync();
    }

    private void GKeyDetect_Click(object sender, RoutedEventArgs e)
    {
        GKeyText.Text = "Press the G-key (F1) now…";
        _settingsPinned = true;
        _gkey.LearnNext = (vk, scan) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _settings.GKeyVk = _gkey.Vk = vk;
            _settings.GKeyScan = _gkey.Scan = scan;
            _settings.Save();
            UpdateGKeyText();
            _settingsPinned = false;
            Status($"G-key set to key 0x{vk:X2} (scan 0x{scan:X2}).");
        }));
    }

    private void UpdateGKeyText() =>
        GKeyText.Text = $"G-key (F1) toggles G-Mode · key 0x{_settings.GKeyVk:X2}";

    private async void Cpu_Click(object sender, RoutedEventArgs e) =>
        // AC level from the button; battery stays on "efficient" (3).
        Report(await _fan.SetCpuBoostAsync(int.Parse((string)((RadioButton)sender).CommandParameter), 3));

    private async void Fan_Click(object sender, RoutedEventArgs e)
    {
        int boost = sender == FanMax ? _settings.MaxFanBoost : 0;
        Report(await _fan.SetFanBoostAsync(Enumerable.Repeat(boost, _fanCount).ToArray()));
        if (boost == 0) SetHint(FanHint, "BIOS controlled", "BIOS");
        else SetHint(FanHint, $"Boost {boost}%");
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
                t = (t + (wave ? 10 : 6)) % 360; // v0.6.3: about 1.7x faster (user found it slow)
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
        SelectSwatch(_lastColor);
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
        SelectSwatch(_lastColor);
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
        SelectSwatch(_lastColor);
        await ApplyColorAsync(_lastColor);
    }

    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateBrightnessText((int)e.NewValue);
    }

    private void UpdateBrightnessText(int v)
    {
        if (BrightnessHint is null || BrightnessValue is null) return;
        SetHint(BrightnessHint, "", $"{v}%");   // Fluent shows the % only beside the slider (v0.8.4)
        BrightnessValue.Text = $"{v}%";
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

    /// <summary>
    /// App accent follows the keyboard / ring color. Black (lights off) keeps the theme default
    /// (Legend cyan #00E5FF, Fluent #4CC2FF; the default cyan lighting also maps to #4CC2FF in Fluent).
    /// Set at App.Resources level, so it is re-applied after every theme swap.
    /// </summary>
    private void ApplyAccent(byte[] c)
    {
        var cyan = Color.FromRgb(0, 229, 255);
        var col = (c[0] + c[1] + c[2]) < 60 ? cyan : Color.FromRgb(c[0], c[1], c[2]);
        bool legend = IsLegend;
        if (!legend && col == cyan) col = Color.FromRgb(0x4C, 0xC2, 0xFF);
        var res = Application.Current.Resources;
        Color A(byte a) => Color.FromArgb(a, col.R, col.G, col.B);
        res["AccentBrush"] = Freeze(new SolidColorBrush(col));
        res["AccentBorder"] = Freeze(new SolidColorBrush(A(0x80)));
        res["AccentFaint"] = Freeze(new SolidColorBrush(A(0x2E)));
        res["AccentGlow"] = Freeze(new SolidColorBrush(A(0x55)));
        res["AccentCheckedBg"] = legend
            ? Freeze(new LinearGradientBrush(A(0x17), A(0x05), 90))
            : Freeze(new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)));
        var top = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        top.GradientStops.Add(new GradientStop(A(0), 0));
        top.GradientStops.Add(new GradientStop(col, 0.18));
        top.GradientStops.Add(new GradientStop(col, 0.5));
        top.GradientStops.Add(new GradientStop(Color.FromRgb(0x7C, 0x4D, 0xFF), 0.82));
        top.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x7C, 0x4D, 0xFF), 1));
        res["TopLineBrush"] = Freeze(top);
        try { Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(col, Wpf.Ui.Appearance.ApplicationTheme.Dark); } catch { }
    }

    // ---------- theme ----------

    private readonly System.Collections.Generic.Dictionary<TextBlock, (string Fluent, string Legend)> _hints = new();

    /// <summary>Header hints: Legend shows them in caps (or its own short form), Fluent as written.</summary>
    private void SetHint(TextBlock tb, string text, string? legend = null)
    {
        _hints[tb] = (text, legend ?? text.ToUpperInvariant());
        tb.Text = IsLegend ? _hints[tb].Legend : text;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        string name = sender == ThemeFluent ? "Fluent" : "Legend";
        if (name == _settings.Theme) return;
        _settings.Theme = name;
        _settings.Save();
        App.ApplyTheme(name);   // swaps the one theme dictionary in App.Resources
        ApplyAccent(_lastColor);
        foreach (var (tb, t) in _hints) tb.Text = IsLegend ? t.Legend : t.Fluent;
        UpdateProfileChip(CurrentProfile);   // Legend caps vs Fluent title case
        Dispatcher.BeginInvoke(new Action(SnapToCorner), DispatcherPriority.Loaded);
    }

    // ---------- gear flyout (v0.8.6: slide up 14 DIP, 160 ms decelerate, quick fade; instant with Reduce motion) ----------

    private bool _settingsPinned;   // "Detect key" keeps the flyout open while it waits for the G-key
    private int _flyGen;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPopup.IsOpen) CloseSettings();
        else OpenSettings();
    }

    private void OpenSettings()
    {
        _flyGen++;
        SettingsPopup.IsOpen = true;
        var shift = Motion.Translate(SettingsPanel);
        if (!Motion.Enabled) { ResetFlyout(); return; }
        Motion.Play(null,
            (SettingsPanel, OpacityProperty, Motion.Tween(0, 1, 90, Motion.Quad)),
            (shift, TranslateTransform.YProperty, Motion.Spline(14, 0, 160, Motion.Decelerate)));
    }

    private void CloseSettings(bool instant = false)
    {
        if (!SettingsPopup.IsOpen) return;
        int gen = ++_flyGen;
        if (instant || !Motion.Enabled || !IsVisible)
        {
            SettingsPopup.IsOpen = false;
            ResetFlyout();
            return;
        }
        try
        {
            Motion.Play(() => { if (gen == _flyGen) { SettingsPopup.IsOpen = false; ResetFlyout(); } },
                (SettingsPanel, OpacityProperty, Motion.Tween(null, 0, 120, Motion.Quad)),
                (Motion.Translate(SettingsPanel), TranslateTransform.YProperty, Motion.Spline(null, 14, 120, Motion.Accelerate)));
        }
        catch { SettingsPopup.IsOpen = false; ResetFlyout(); }
    }

    private void ResetFlyout()
    {
        Motion.Set(SettingsPanel, OpacityProperty, 1);
        Motion.Set(Motion.Translate(SettingsPanel), TranslateTransform.YProperty, 0);
    }

    private static bool IsWithin(DependencyObject? d, DependencyObject ancestor)
    {
        for (var x = d; x is not null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == ancestor) return true;
        return false;
    }

    private void ReduceMotion_Click(object sender, RoutedEventArgs e)
    {
        _settings.ReduceMotion = ReduceMotionToggle.IsChecked == true;
        Motion.Enabled = !_settings.ReduceMotion;
        _settings.Save();
        if (_settings.ReduceMotion) { ResetCard(); ResetFlyout(); Motion.Set(ProfileChip, OpacityProperty, 1); }
    }

    private void SelectSwatch(byte[] c)
    {
        string tag = string.Join(",", c);
        RadioButton? match = null;
        foreach (var rb in FindSwatches()) if ((rb.Tag as string) == tag) match = rb;
        if (match is not null) match.IsChecked = true;
        else if (c.All(v => v == 0)) foreach (var rb in FindSwatches()) rb.IsChecked = false;
        else CustomSwatch.IsChecked = true;
    }

    private System.Collections.Generic.IEnumerable<RadioButton> FindSwatches() =>
        ((Panel)CustomSwatch.Parent).Children.OfType<RadioButton>();

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }


    private void Report(CliResult r)
    {
        var msg = (r.Ok ? r.Output : (r.Error.Length > 0 ? r.Error : r.Output)).Trim();
        var last = msg.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        Status(last);
    }

    private void Status(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- RAM clean (click = quick, right-click = deep) ----------

    private bool _cleaning;
    private void RamCard_LeftClick(object sender, MouseButtonEventArgs e) => _ = CleanRamAsync(deep: false);
    private void RamCard_RightClick(object sender, MouseButtonEventArgs e) { e.Handled = true; _ = CleanRamAsync(deep: true); }

    private string Cased(string s) => IsLegend ? s.ToUpperInvariant() : s;

    private async Task CleanRamAsync(bool deep)
    {
        if (_cleaning) return;
        _cleaning = true;
        CleanText.Text = Cased(deep ? "Deep cleaning…" : "Cleaning…");
        try
        {
            var (freed, error) = await MemoryCleaner.CleanAsync(deep);
            if (error is not null) Status(error);
            CleanText.Text = Cased(error is not null ? "Clean failed" : $"Freed {freed:0.0} GB");
            if (SystemStats.Ram() is { } ram) SetMem(RamText, RamTextUnit, RamBar, ram.Used, ram.Total);
            await Task.Delay(4000);
        }
        finally
        {
            CleanText.SetResourceReference(TextBlock.TextProperty, "Str.Clean");
            _cleaning = false;
        }
    }

    // ---------- GPU mode (v0.8: preview only, the buttons don't switch anything yet) ----------

    /// <summary>Hint = detected mode (read-only). The buttons stay on Optimus until they're wired up.</summary>
    private async Task RefreshGpuHintAsync()
    {
        var mode = await Task.Run(() => GpuMode.Current(GpuMode.Adapters()));
        var (fluent, legend) = mode switch
        {
            GpuModeKind.IGpuOnly => ("iGPU only · RTX off", "iGPU ONLY · RTX off"),
            GpuModeKind.Optimus => ("Optimus · RTX 3070 Ti", "OPTIMUS · 3070 Ti"),
            GpuModeKind.DGpuOnly => ("dGPU only · RTX 3070 Ti", "dGPU ONLY · 3070 Ti"),
            GpuModeKind.Ultimate => ("Ultimate · RTX 3070 Ti", "ULTIMATE · 3070 Ti"),
            _ => ("RTX 3070 Ti", "3070 Ti")
        };
        SetHint(GpuHint, fluent, legend);
    }

    private void GpuPreview_Click(object sender, RoutedEventArgs e)
    {
        GpuOptimus.IsChecked = true;   // visual only for now
        Status("GPU mode switching is coming soon. Nothing was changed.");
    }

    /// <summary>"Check Ultimate support": GPU / BIOS WMI report, shown and copied to the clipboard.</summary>
    private async void GpuCheck_Click(object sender, RoutedEventArgs e)
    {
        GpuCheckButton.IsEnabled = false;
        try
        {
            var report = await Task.Run(GpuMode.Report);
            bool copied = false;
            try { Clipboard.SetText(report); copied = true; } catch { }
            Status(copied ? "GPU report copied. Paste it to me." : "Couldn't copy the GPU report.");
            MessageBox.Show(report + (copied ? "\n(Copied to the clipboard.)" : ""), "Alien Controls · Ultimate support",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { GpuCheckButton.IsEnabled = true; }
    }

    // ---------- refresh rate (read on window show and after a change; never polled) ----------

    private void RefreshDisplayRate()
    {
        var s = DisplayRefresh.Read();
        if (s is null)
        {
            Hz60.IsEnabled = Hz240.IsEnabled = false;
            SetHint(RefreshHint, "Unknown");
            return;
        }
        Hz60.IsEnabled = DisplayRefresh.Match(s, 60) is not null;
        Hz240.IsEnabled = DisplayRefresh.Match(s, 240) is not null;
        Hz60.IsChecked = DisplayRefresh.IsActive(s, 60);
        Hz240.IsChecked = DisplayRefresh.IsActive(s, 240);
        SetHint(RefreshHint, $"{s.CurrentHz} Hz", $"{s.CurrentHz} Hz");
        Hz60.ToolTip = Hz60.IsEnabled ? null : $"60 Hz isn't offered at {s.Width}x{s.Height}";
        Hz240.ToolTip = Hz240.IsEnabled ? null : $"240 Hz isn't offered at {s.Width}x{s.Height}";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        int hz = sender == Hz60 ? 60 : 240;
        var err = await Task.Run(() => DisplayRefresh.Set(hz));
        Status(err ?? $"Display set to {hz} Hz.");
        RefreshDisplayRate();
        UpdateProfileFromState();
    }

    // ---------- tray profiles (power mode + refresh rate; never GPU mode, never Ultimate) ----------

    public async Task ApplyProfileAsync(TrayProfile p)
    {
        SetProfile(p);
        if (p == TrayProfile.Custom) return;
        if (Profiles.PowerIndex(p, _settings) is { } idx)
        {
            if (_gmodeOn || await _fan.GetGModeAsync() == true) { await _fan.SetGModeAsync(false); _gmodeOn = false; }
            Report(await _fan.SetPowerAsync(idx));
            _lastPowerIndex = idx;
        }
        if (Profiles.RefreshHz(p) is { } hz)
        {
            var err = await Task.Run(() => DisplayRefresh.Set(hz));
            if (err is not null) Status(err);
        }
        if (IsVisible) { RefreshDisplayRate(); await RefreshStateAsync(); }
        await SyncIndicatorsAsync();
    }

    private void SetProfile(TrayProfile p)
    {
        if (_settings.Profile != p.ToString()) { _settings.Profile = p.ToString(); _settings.Save(); }
        UpdateProfileChip(p);
        ProfileChanged?.Invoke(p);
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
        // v0.8.2: the new colour fades in (v0.8.6: Opacity 0.2 -> 1, 250 ms) when the profile actually changes on screen.
        bool changed = _chipProfile is { } prev && prev != p;
        _chipProfile = p;
        if (changed && Motion.Enabled && ProfileChip.IsVisible)
            Motion.Play(null, (ProfileChip, OpacityProperty, Motion.Tween(0.2, 1, 250, Motion.Quad)));
        ProfileChipText.Text = IsLegend ? label.ToUpperInvariant() : label;
        ProfileChipText.Foreground = B(255);
        ProfileChipDot.Fill = B(255);
        ProfileChip.BorderBrush = B(0x66);   // ~40%
        ProfileChip.Background = B(0x17);    // ~9%
        ProfileChip.ToolTip = $"Profile: {label} ({Profiles.Describe(p)})";
    }

    /// <summary>After a manual change: the profile that matches mode + refresh rate, otherwise Custom.</summary>
    private void UpdateProfileFromState()
    {
        var match = TrayProfile.Custom;
        if (!_gmodeOn)
        {
            var rate = DisplayRefresh.Read();
            foreach (TrayProfile p in new[] { TrayProfile.Battery, TrayProfile.Quiet, TrayProfile.Balanced, TrayProfile.Performance })
                if (Profiles.PowerIndex(p, _settings) == _lastPowerIndex && rate is not null && DisplayRefresh.IsActive(rate, Profiles.RefreshHz(p)!.Value))
                { match = p; break; }
        }
        SetProfile(match);
    }
}
