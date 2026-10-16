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
    private readonly MacroKeys _macro = new();   // v0.9.10: F2 preset key (raw HID)
    private bool _f2Dirty;                        // F2 tint changed; next keyboard sync writes a frame
    private int _lastPowerIndex;
    private byte[] _lastColor = { 0, 229, 255 };
    // v0.9.13: status keys are event driven (StateWatch); this is only a slow safety net (was 1 s polling)
    private readonly DispatcherTimer _indicatorTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private StateWatch? _watch;
    private int _watchQueued;
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

    /// <summary>v0.9.8: what the chip shows: G-Mode while it's on, else the saved preset.</summary>
    private TrayProfile ShownProfile => _gmodeOn ? TrayProfile.GMode : CurrentProfile;

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
        F2PresetsToggle.IsChecked = _settings.F2Presets;
        F3OpensToggle.IsChecked = _settings.F3Opens;
        _macro.Pressed += k =>
        {
            if (k == 0) Dispatcher.BeginInvoke(new Action(async () => await CyclePresetAsync()));
            else if (k is 1 or 2 && _settings.F3Opens)   // v0.9.16 F3; v1.1: F3 compact, F4 full
                Dispatcher.BeginInvoke(new Action(() => ((App)Application.Current).ToggleMain(full: k == 2)));
        };
        SetCompact(true);
        SizeChanged += OnSizeChangedKeepCorner;
        _macro.Install();
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
        _watch = new StateWatch();
        _watch.Changed += () =>
        {
            // coalesce bursts (a volume drag fires dozens of callbacks)
            if (System.Threading.Interlocked.Exchange(ref _watchQueued, 1) == 1) return;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                await Task.Delay(80);
                System.Threading.Interlocked.Exchange(ref _watchQueued, 0);
                await SyncIndicatorsAsync();
            }));
        };
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
                PlayOpen();   // v0.8.8 native Quick Settings style slide (instant with Reduce motion)
                int gen = _showGen;
                UpdateProfileChip(ShownProfile);
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
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) CloseSettings(instant: true);
            else if (WindowState == WindowState.Normal && _minSlid)
            {
                _minSlid = false;   // v0.8.9: restored from a slid minimize: rise back up from below the screen
                _showGen++;
                _hiding = false;
                if (Motion.Enabled) SlideIn(); else SnapToCorner();
            }
        };

        SizeChanged += (_, _) => SnapToCorner();
        SystemParameters.StaticPropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.BeginInvoke(new Action(SnapToCorner));
        };

        UpdateProfileChip(ShownProfile);
        // v1.0.0-rc1: informational version keeps the -rc suffix; drop the +commit the SDK appends.
        var ver = (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MainWindow).Assembly)?.InformationalVersion
                   ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1").Split('+')[0];
        VersionText.Text = "v" + ver;
        AboutText.Text = $"Alien Controls {ver}. Fans, power and lighting by alienfx-tools (T-Troll, MIT). Not made by Dell or Alienware. Personal project built with AI help, use at your own risk.";
        StartupToggle.IsChecked = Startup.IsEnabled();
        InitOc();   // v0.9.7 GPU overclock (G-Mode on AC only)
        InitAuto(); // v0.9.11 auto preset + toasts

        if (!_fan.IsAvailable || !_fx.IsAvailable)
            Status((_fan.IsAvailable ? "alienfx-cli.exe" : _fx.IsAvailable ? "alienfan-cli.exe" : "alienfan-cli.exe and alienfx-cli.exe")
                   + " missing. Put it next to AlienControl.exe (or in a tools folder there), then restart the app.");
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
        if (_slide?.Active == true || _openParked || _minSlid) return;   // v0.8.8: the slide owns the position until it lands
        var (l, t) = CornerPosition();
        Left = l;
        Top = t;
    }

    private (double Left, double Top) CornerPosition()
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
        return (right - w - 4, Math.Max(0, bottom - h - 4));
    }

    protected override void OnContentRendered(EventArgs e) { base.OnContentRendered(e); SnapToCorner(); }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e) { /* pinned; no dragging */ }
    /// <summary>v0.8.9: minimize slides down into the screen edge like hide, then minimizes (taskbar button stays).
    /// The restore position is left below the screen, so a restore (taskbar, tray, Alt+Tab) slides it back up.</summary>
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        CloseSettings(instant: true);
        if (!Motion.Enabled || _hiding) { WindowState = WindowState.Minimized; return; }
        int gen = ++_showGen;
        try
        {
            var h = Hwnd;
            var r = WindowSlide.Rect(h);
            Slide.Run(r.Left, WindowSlide.MonitorBottom(h) + 2, HideMs, CubicBezier.Accelerate, () =>
            {
                if (gen != _showGen) return;
                _minSlid = true;
                WindowState = WindowState.Minimized;
            });
        }
        catch { WindowState = WindowState.Minimized; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => HideToTray();

    // ---------- v0.8.8 window open / hide: Windows 11 Quick Settings style slide, done natively ----------
    // v0.8.6/0.8.7 slid the card INSIDE the transparent window; the laptop recording showed not one in-between
    // frame (WPF never presented them for the layered window). Now the whole HWND moves (WindowSlide, SetWindowPos
    // per frame): the compositor moves the already-drawn window, rising out of the bottom screen edge on open and
    // dropping back past it on hide. The per-frame hook lives only for the 250 / 170 ms of a slide.

    private const int OpenMs = 250, HideMs = 170;
    private int _showGen;     // bumps on every show/hide so a stale hide never fires after a re-show
    private bool _hiding;
    private bool _minSlid;    // v0.8.9: minimized by sliding down; the restore slides it back up
    private bool _openParked; // PrepareShow parked the hidden window below the screen; the slide starts once it's drawn
    private WindowSlide? _slide;
    private WindowSlide Slide => _slide ??= new WindowSlide(() => new System.Windows.Interop.WindowInteropHelper(this).Handle);
    private IntPtr Hwnd => new System.Windows.Interop.WindowInteropHelper(this).Handle;

    /// <summary>The snapped corner position in device pixels (what SetWindowPos takes).</summary>
    private (int X, int Y) RestDevice()
    {
        var (l, t) = CornerPosition();
        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        return ((int)Math.Round(l * m.M11), (int)Math.Round(t * m.M22));
    }

    /// <summary>App.ShowMain calls this before Show(). Hidden: park the window just below the screen so it is drawn
    /// out of sight and then rises. Mid-hide: reverse from where it is.</summary>
    public void PrepareShow()
    {
        if (_hiding) { _hiding = false; _showGen++; SlideIn(); return; }
        if (IsVisible || !Motion.Enabled) return;
        var (l, _) = CornerPosition();
        Left = l;
        Top = SystemParameters.PrimaryScreenHeight + 2;   // fully below the bottom edge of the screen
        _openParked = true;
    }

    /// <summary>Called when the window becomes visible.</summary>
    private void PlayOpen()
    {
        _showGen++;
        _hiding = false;
        if (!Motion.Enabled || !_openParked) { _openParked = false; Slide.Stop(); SnapToCorner(); return; }
        // Wait two rendered frames, so layout (SizeToContent) and the first paint are done while it's out of sight.
        int gen = _showGen, frames = 0;
        EventHandler? onFrame = null;
        onFrame = (_, _) =>
        {
            if (++frames < 2) return;
            CompositionTarget.Rendering -= onFrame;   // one-shot: never keeps a per-frame hook alive
            _openParked = false;
            if (gen == _showGen && !_hiding && IsVisible) SlideIn();
        };
        CompositionTarget.Rendering += onFrame;
    }

    private void SlideIn()
    {
        int gen = _showGen;
        var (x, y) = RestDevice();
        Slide.Run(x, y, OpenMs, CubicBezier.Decelerate, () => { if (gen == _showGen && !_hiding) SnapToCorner(); });
    }

    /// <summary>Slide down past the screen edge over 170 ms, THEN Hide(). Instant with Reduce motion or minimized;
    /// a one-shot 400 ms safety net hides it if the slide never reports back.</summary>
    public void HideToTray()
    {
        CloseSettings(instant: true);
        if (!IsVisible) return;
        if (!Motion.Enabled || WindowState == WindowState.Minimized || _hiding) { HideNow(); return; }
        _hiding = true;
        _openParked = false;
        int gen = ++_showGen;
        try
        {
            var h = Hwnd;
            var r = WindowSlide.Rect(h);
            Slide.Run(r.Left, WindowSlide.MonitorBottom(h) + 2, HideMs, CubicBezier.Accelerate,
                () => { if (gen == _showGen && _hiding) HideNow(); });
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
        _minSlid = false;
        _showGen++;
        Slide.Stop();
        Hide();
        _ = TrimSoonAsync(_showGen);
    }

    // ---------- v0.9.13 idle trim: give memory back while the app sits in the tray ----------
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    private async Task TrimSoonAsync(int gen)
    {
        await Task.Delay(3000);
        if (gen != _showGen || IsVisible) return;   // reopened meanwhile
        TrimMemory();
    }

    private static void TrimMemory()
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSize(p.Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }

    public void AllowClose() { ResetOcOnExit(); RestoreBoostOnExit(); StopAnimation(); _allowClose = true; _stats.Dispose(); _power.Dispose(); _gkey.Dispose(); _macro.Dispose(); _indicatorTimer.Stop(); _watch?.Dispose(); }

    // ---------- status keys ----------

    private async Task StartupLightingAsync()
    {
        if (!_fx.IsAvailable) return;
        _gmodeOn = await _fan.GetGModeAsync() == true;
        await _fx.ResetHardwareDimAsync();
        ReadIndicatorStates();
        UpdateF2Tint(ShownProfile);
        // Reapply the user's color after boot (firmware resets to its default); otherwise leave firmware lighting alone.
        if (_settings.LastColor is { Length: 3 } c)
        {
            if (_settings.LastEffect >= 100) await ApplyLightingAsync(c);
            else if (_settings.LastEffect == 0) await _fx.SetAllAsync(c[0], c[1], c[2], _indicators, _settings.Brightness);
            else await _fx.SetEffectAsync(_settings.LastEffect, c, _settings.Brightness);
            _lightsManaged = true;
        }
        _indicatorTimer.Start();
        if (!IsVisible) _ = TrimSoonAsync(_showGen);   // started to tray: trim once things have settled
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
        Set(_settings.SpeakerMuteKey, _watch?.SpeakerMuted() ?? SystemState.SpeakerMuted());
        Set(_settings.MicMuteKey, _watch?.MicMuted() ?? SystemState.MicMuted());
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
            if (changed.Count > 0 || _f2Dirty) { _f2Dirty = false; await _fx.SetKeyboardAsync(_lastColor, _indicators, _settings.Brightness); }
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
        await SyncOcAsync();
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

    // v0.8.5: power has its own 1 s timer (perf counter + NVML are cheap; the 2 s sensor poll reads WMI since 1.1, alienfan-cli as fallback).
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
        await SyncOcAsync();
        await SyncIndicatorsAsync();
    }

    /// <summary>G-key press: toggle G-Mode; turning it off returns to the last power mode.</summary>
    private async Task ToggleGModeAsync()
    {
        // v1.0.0-rc1: toast + sound right on the key press (they used to wait for the CLI calls, ~1 s).
        // _gmodeOn is kept in sync by every read, so no extra getgmode call before acting.
        var current = _gmodeOn;
        if (!current)
            Notify(TrayProfile.GMode, "F1 · fans max" + (_settings.OcOn && OnAc && (_settings.OcCore | _settings.OcMem) != 0
                ? $" · OC +{_settings.OcCore} / +{_settings.OcMem}" : ""), Sounds.Kind.GModeOn);
        else
            Notify(CurrentProfile, $"F1 · G-Mode off · back to {Profiles.Label(CurrentProfile)}", Sounds.Kind.GModeOff);
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
        await SyncOcAsync();
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

    private async void Cpu_Click(object sender, RoutedEventArgs e)
    {
        // AC level from the button; battery stays on "efficient" (3).
        int level = int.Parse((string)((RadioButton)sender).CommandParameter);
        Report(await _fan.SetCpuBoostAsync(level, 3));
        if (await CpuFreq.ApplyAsync(level, 3) is { } err) Status(err);   // v0.9.4: Efficient = 3.0 GHz cap (v0.9.5)
        _boostForced = false;   // v0.9.9: an explicit pick wins, even inside G-Mode
    }

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
        AccentColor = col;
        (Application.Current as App)?.UpdateTrayMenu(legend, col);   // v0.8.7: tray menu follows theme + accent
        // v0.8.10: line icons bind Stroke to their inherited TextElement.Foreground; WPF updated them one click late
        // (the old accent stayed until the next change). Re-read the bindings and repaint once, after the resource swap.
        Dispatcher.BeginInvoke(new Action(() => RefreshIcons(this)), DispatcherPriority.Loaded);
    }

    /// <summary>One-shot walk (only on an accent or theme change): re-reads icon Stroke / Fill bindings and repaints them.</summary>
    private static void RefreshIcons(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ContentControl or ContentPresenter)
                child.InvalidateProperty(TextElement.ForegroundProperty);   // re-evaluate the style / trigger value
            if (child is System.Windows.Shapes.Shape shape)
            {
                System.Windows.Data.BindingOperations.GetBindingExpression(shape, System.Windows.Shapes.Shape.StrokeProperty)?.UpdateTarget();
                System.Windows.Data.BindingOperations.GetBindingExpression(shape, System.Windows.Shapes.Shape.FillProperty)?.UpdateTarget();
                shape.InvalidateVisual();
            }
            RefreshIcons(child);
        }
    }

    /// <summary>v0.8.7: current accent and theme, read by the tray menu.</summary>
    public Color AccentColor { get; private set; } = Color.FromRgb(0, 229, 255);
    public bool IsLegendTheme => IsLegend;

    /// <summary>v0.8.7: visible, not minimized and not already sliding away (tray left click then hides it).</summary>
    public bool IsShownOpen => IsVisible && WindowState != WindowState.Minimized && !_hiding;

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
        UpdateProfileChip(ShownProfile);   // Legend caps vs Fluent title case
        UpdateOcUi();
        Dispatcher.BeginInvoke(new Action(SnapToCorner), DispatcherPriority.Loaded);
    }

    // ---------- gear flyout (v0.8.8: the popup's own window slides up 14 DIP natively, 160 ms decelerate;
    // out 120 ms accelerate; instant with Reduce motion). Same reason as the main window: content animations
    // inside a transparent window weren't being presented. ----------

    private bool _settingsPinned;   // "Detect key" keeps the flyout open while it waits for the G-key
    private int _flyGen;
    private WindowSlide? _flySlide;
    private int _flyRestY = int.MinValue;
    private IntPtr FlyHwnd => (PresentationSource.FromVisual(SettingsPanel) as System.Windows.Interop.HwndSource)?.Handle ?? IntPtr.Zero;
    private WindowSlide FlySlide => _flySlide ??= new WindowSlide(() => FlyHwnd);
    private int FlyOffset => (int)Math.Round(14 * (PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0));

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPopup.IsOpen) CloseSettings();
        else OpenSettings();
    }

    private void OpenSettings()
    {
        int gen = ++_flyGen;
        bool reopening = SettingsPopup.IsOpen && FlySlide.Active;   // clicked again while it was sliding out
        SettingsPopup.IsOpen = true;
        ResetFlyout();
        if (!Motion.Enabled) return;
        var h = FlyHwnd;
        if (h == IntPtr.Zero) return;
        var r = WindowSlide.Rect(h);
        if (!reopening || _flyRestY == int.MinValue) _flyRestY = r.Top;
        if (!reopening) WindowSlide.Move(h, r.Left, _flyRestY + FlyOffset);
        FlySlide.Run(r.Left, _flyRestY, 160, CubicBezier.Decelerate, null);
    }

    private void CloseSettings(bool instant = false)
    {
        if (!SettingsPopup.IsOpen) return;
        int gen = ++_flyGen;
        var h = FlyHwnd;
        if (instant || !Motion.Enabled || !IsVisible || h == IntPtr.Zero)
        {
            _flySlide?.Stop();
            SettingsPopup.IsOpen = false;
            return;
        }
        try
        {
            var r = WindowSlide.Rect(h);
            if (_flyRestY == int.MinValue) _flyRestY = r.Top;
            FlySlide.Run(r.Left, _flyRestY + FlyOffset, 120, CubicBezier.Accelerate,
                () => { if (gen == _flyGen) SettingsPopup.IsOpen = false; });
        }
        catch { _flySlide?.Stop(); SettingsPopup.IsOpen = false; }
    }

    /// <summary>Clears any content animation left on the panel (older builds animated it directly).</summary>
    private void ResetFlyout()
    {
        Motion.Set(SettingsPanel, OpacityProperty, 1);
        SettingsPanel.RenderTransform = Transform.Identity;
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
        if (_settings.ReduceMotion) { ResetFlyout(); Motion.Set(ProfileChip, OpacityProperty, 1); }
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
        Status(r.Ok ? last : FriendlyError(last));
    }

    /// <summary>v0.9.15: plain-words version of a CLI failure. Keeps the raw line at the end for bug reports.</summary>
    private static string FriendlyError(string raw)
    {
        var l = raw.ToLowerInvariant();
        string tool = l.Contains("alienfx") ? "Lighting" : "Fans / power";
        if (l.Contains("not found next to")) return raw;   // already plain (CliRunner)
        if (l.Contains("timed out")) return $"{tool} didn't answer in time. Nothing changed, try again.";
        if (l.Contains("access") && l.Contains("denied")) return $"{tool}: access denied. Run Alien Controls as administrator.";
        if (l.Contains("no device") || l.Contains("not found") || l.Contains("can't find") || l.Contains("cannot find"))
            return $"{tool}: device not found. Close Alienware Command Center / AWCC services and try again.";
        if (raw.Length == 0) return $"{tool} failed with no message. Try again.";
        return $"{tool} failed: {raw}";
    }

    // ---------- v0.9.15: reset settings ----------

    private async void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        CloseSettings(instant: true);
        var pick = await ShowGpuDialog("Reset settings?",
            "Theme, toggles, colors, effect, G-key, OC values and presets go back to defaults, then the app restarts. "
            + "Run on startup, the BIOS and the GPU mode stay as they are. GPU OC goes back to +0.",
            null, null, ("cancel", "Cancel", false), ("reset", "Reset", true));
        if (pick != "reset") return;
        ((App)Application.Current).RestartFresh();
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

    // ---------- GPU mode ----------
    // v0.9.2: two modes, Advanced Optimus and Ultimate (Dell BIOS HybridGraphics + restart). NVIDIA has no API for its
    // display mode (Procmon 2026-10-15), so Auto / Optimus / GPU only stay in NVIDIA's tray popup and the hint just reads them.
    // Read on show, after a change, on display changes and on focus; never polled.

    private GpuMode.State _gpu = new(GpuModeKind.Unknown, false, null);
    private bool _gpuBusy, _gpuDisplayHooked;

    private Task RefreshGpuHintAsync() => RefreshGpuAsync(withBios: true);

    private async Task RefreshGpuAsync(bool withBios)
    {
        var prev = _gpu;
        var setUtc = _settings.GpuSwitchSetUtc;
        var st = await Task.Run(() => GpuMode.Read(withBios, setUtc));
        if (!withBios)
        {
            // only the BIOS read can tell Ultimate apart: keep what the last full read said
            st = st with { BiosUltimate = prev.BiosUltimate };
            if (prev.Kind == GpuModeKind.Ultimate) st = st with { Kind = GpuModeKind.Ultimate, Auto = false };
        }
        _gpu = st;
        ShowGpuState();
        if (!_gpuDisplayHooked)
        {
            _gpuDisplayHooked = true;
            // NVIDIA's switch (and Auto) reconfigures the panel: re-read once it settles. Event only, no timer.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(1500);
                if (IsVisible) await RefreshGpuAsync(withBios: false);
            });
            // Auto <-> Optimus while Intel stays on the panel changes no display, only NVIDIA's flag: re-read on focus.
            Activated += async (_, _) =>
            {
                if (IsVisible && !_gpuBusy) await RefreshGpuAsync(withBios: false);
            };
        }
    }

    private void ShowGpuState()
    {
        var k = _gpu.Kind;
        bool ult = k == GpuModeKind.Ultimate;
        GpuOptimus.IsChecked = !ult && k != GpuModeKind.Unknown;
        GpuUltimate.IsChecked = ult;
        GpuOptimus.ToolTip = ult ? "Back to hybrid: turns the Intel GPU back on in the BIOS. Needs a restart."
            : "Intel + RTX. Pick Auto / Optimus / GPU only from NVIDIA's tray icon.";

        // v0.9.2: Advanced Optimus = NVIDIA's mode + the GPU driving the screen. Ultimate = just the RTX.
        bool rtx = k == GpuModeKind.DGpuOnly;
        // v0.9.5 (Amine): full GPU names in both themes, only the mode word follows the theme's casing
        string gpu = rtx ? "RTX 3070 Ti" : "Intel Iris Xe";
        var (fluent, legend) = k switch
        {
            GpuModeKind.Ultimate => ("RTX 3070 Ti", "RTX 3070 Ti"),
            GpuModeKind.Unknown => ("", ""),
            _ when _gpu.Auto => ($"Auto · {gpu}", $"AUTO · {gpu}"),
            GpuModeKind.DGpuOnly => ($"dGPU only · {gpu}", $"dGPU ONLY · {gpu}"),
            _ => ($"Optimus · {gpu}", $"OPTIMUS · {gpu}")
        };
        if (_gpu.PendingRestart)
            (fluent, legend) = _gpu.BiosUltimate == true
                ? ("Ultimate after restart", "ULTIMATE AFTER RESTART")
                : ("Adv. Optimus after restart", "ADV. OPTIMUS AFTER RESTART");
        SetHint(GpuHint, fluent, legend);
    }

    private async void GpuMode_Click(object sender, RoutedEventArgs e)
    {
        bool toUlt = sender == GpuUltimate;
        ShowGpuState();   // the row only moves once the switch really happened
        if (_gpuBusy) return;
        _gpuBusy = true;
        try { await SwitchGpuAsync(toUlt); }
        catch (Exception ex) { Status("GPU switch failed: " + ex.Message); }
        finally
        {
            _gpuBusy = false;
            await RefreshGpuAsync(withBios: true);
        }
    }

    /// <summary>Advanced Optimus <-> Ultimate through Dell BIOS HybridGraphics. Clicking the running mode does nothing,
    /// except it cancels a switch queued for the next restart.</summary>
    private async Task SwitchGpuAsync(bool toUlt)
    {
        var cur = _gpu.Kind;
        if (cur == GpuModeKind.Unknown) { Status("Couldn't read the GPU state. Nothing was changed."); return; }
        bool isUlt = cur == GpuModeKind.Ultimate;

        if (toUlt == isUlt)
        {
            if (!_gpu.PendingRestart) return;
            var undo = await Task.Run(() => GpuMode.SetUltimate(isUlt));
            if (undo is null) { _settings.GpuSwitchSetUtc = null; _settings.Save(); }
            Status(undo ?? "Switch cancelled. No restart needed.");
            return;
        }
        if (_gpu.PendingRestart) { Status("Already set. It takes effect after your next restart."); return; }

        var pick = await ShowGpuDialog(
            toUlt ? "Switch to Ultimate?" : "Back to Advanced Optimus?",
            toUlt ? "The BIOS turns the Intel GPU off and the RTX drives everything: best FPS, shortest battery. "
                    + "NVIDIA's display modes come back when you switch back. Needs a restart."
                  : "The BIOS turns the Intel GPU back on, so NVIDIA's Auto / Optimus / GPU only modes come back. Needs a restart.",
            null, null,
            ("cancel", "Cancel", false), ("later", "Restart later", false), ("now", "Restart now", true));
        if (pick is null or "cancel") return;
        if (toUlt && cur == GpuModeKind.IGpuOnly)
        {
            // RTX disabled by an older build: never boot into Ultimate like that (black screen)
            var en = await GpuMode.SetDgpuEnabledAsync(true);
            if (en is not null) { Status(en); return; }
        }
        var err = await Task.Run(() => GpuMode.SetUltimate(toUlt));
        if (err is not null) { Status(err); return; }
        _settings.GpuSwitchSetUtc = DateTime.UtcNow;
        _settings.Save();
        if (pick == "now") { GpuMode.RestartNow(); return; }
        Status(toUlt ? "Ultimate is set. It takes effect after your next restart." : "Advanced Optimus is set. It takes effect after your next restart.");
    }

    // ----- in-window dialog -----

    private TaskCompletionSource<string?>? _gpuDlg;

    private Task<string?> ShowGpuDialog(string title, string body, System.Collections.Generic.IList<string>? list, string? listTitle,
        params (string Id, string Text, bool Primary)[] buttons)
    {
        _gpuDlg?.TrySetResult(null);
        var tcs = new TaskCompletionSource<string?>();
        _gpuDlg = tcs;
        GpuDlgTitle.Text = IsLegend ? title.ToUpperInvariant() : title;
        GpuDlgBody.Text = body;
        GpuDlgListTitle.Text = listTitle ?? "";
        GpuDlgListTitle.Visibility = string.IsNullOrEmpty(listTitle) ? Visibility.Collapsed : Visibility.Visible;
        GpuDlgList.ItemsSource = list;
        GpuDlgList.Visibility = list is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        GpuDlgButtons.Children.Clear();
        foreach (var b in buttons)
        {
            var btn = new Button { Content = b.Text, Margin = new Thickness(8, 4, 0, 0) };
            btn.SetResourceReference(StyleProperty, "GhostButton");
            if (b.Primary) btn.SetResourceReference(ForegroundProperty, "AccentBrush");
            var id = b.Id;
            btn.Click += (_, _) => CloseGpuDialog(id);
            GpuDlgButtons.Children.Add(btn);
        }
        GpuDialogLayer.Opacity = 1;
        GpuDialogLayer.Visibility = Visibility.Visible;
        if (Motion.Enabled)
            GpuDialogLayer.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        return tcs.Task;
    }

    private void CloseGpuDialog(string? id)
    {
        GpuDialogLayer.BeginAnimation(OpacityProperty, null);
        GpuDialogLayer.Visibility = Visibility.Collapsed;
        var t = _gpuDlg;
        _gpuDlg = null;
        t?.TrySetResult(id);
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

    private void SetProfile(TrayProfile p)
    {
        if (p != TrayProfile.GMode && _settings.Profile != p.ToString()) { _settings.Profile = p.ToString(); _settings.Save(); }
        UpdateProfileChip(p);
        UpdateF2Tint(p);
        ProfileChanged?.Invoke(p);
    }

    // ---------- v0.9.10 F2 preset key ----------

    /// <summary>F2 press: Quiet > Balanced > Performance > Quiet (v0.9.11: on battery Performance > Battery > Quiet).
    /// Battery/Custom start at Quiet. G-Mode: F1 owns it, do nothing.</summary>
    private async Task CyclePresetAsync()
    {
        if (!_settings.F2Presets || _gmodeOn) return;
        var cur = CurrentProfile;
        var next = NextPreset(cur);
        // v1.0.0-rc1: toast first, then apply (no ~1 s wait). Into / out of Battery changes the refresh rate,
        // which blanks the screen, so those still toast after the switch (Notify waits for the screen).
        bool rate = cur == TrayProfile.Battery || next == TrayProfile.Battery;
        void Show() => Notify(next, $"F2 · next: {Profiles.Label(NextPreset(next))}", Sounds.Kind.Tick, cycle: next != TrayProfile.Battery);
        if (!rate) Show();
        await ApplyProfileAsync(next);
        if (rate) Show();
    }

    private static TrayProfile NextPreset(TrayProfile p) => p switch
    {
        TrayProfile.Quiet => TrayProfile.Balanced,
        TrayProfile.Balanced => TrayProfile.Performance,
        TrayProfile.Performance => OnAc ? TrayProfile.Quiet : TrayProfile.Battery,
        _ => TrayProfile.Quiet,
    };

    /// <summary>F2 key color for the shown preset; none (normal key color) in G-Mode, Battery, Custom or when off.</summary>
    private void UpdateF2Tint(TrayProfile p)
    {
        // v1.0.0-rc1: F2 tint removed (Amine: disorienting). F2 always follows the keyboard color; this only
        // clears a tint left from an older build.
        byte[]? c = null;
        var key = _settings.F2Key;
        var had = _fx.KeyOverrides.TryGetValue(key, out var old) ? old : null;
        if (c is null) _fx.KeyOverrides.Remove(key); else _fx.KeyOverrides[key] = c;
        if (!(had ?? Array.Empty<byte>()).AsSpan().SequenceEqual(c ?? Array.Empty<byte>())) _f2Dirty = true;
    }

    private void F3Opens_Click(object sender, RoutedEventArgs e)
    {
        _settings.F3Opens = F3OpensToggle.IsChecked == true;
        _settings.Save();
    }

    private async void F2Presets_Click(object sender, RoutedEventArgs e)
    {
        _settings.F2Presets = F2PresetsToggle.IsChecked == true;
        _settings.Save();
        UpdateF2Tint(ShownProfile);
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
