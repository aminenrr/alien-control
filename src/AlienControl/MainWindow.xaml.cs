using System;
using AlienControl.Overlay;
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

/// <summary>
/// The main window: fields, constructor, close handling, footer actions and small helpers (accent, hints, swatches,
/// status line, RAM clean, reset settings). 2.0.0-beta1.4 (ui review, MainWindow split): the rest lives in partials,
/// moved without behaviour change: MainWindow.Window.cs (placement, slide, trim, AllowClose), .Sensors.cs (reads),
/// .Modes.cs (power modes, G-Mode, command gate, refresh rate), .Gpu.cs (GPU MODE row, RTX off, GPU dialog),
/// .Lights.cs (colours, effects, animation, status keys, zones), .Presets.cs (presets, F2, chip, header button),
/// .Settings.cs, .Tabs.cs, .Oc.cs, .Kbd.cs and .Auto.cs.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AlienFan _fan = new();
    private readonly AlienFx _fx;
    private readonly Settings _settings = Settings.Load();
    private readonly GKeyHook _gkey;
    private readonly MacroKeys _macro = new();   // v0.9.10: F2 preset key (raw HID)
    private bool _frameDirty;                     // the next status-key sync writes a full keyboard frame even without a change
    private int _lastPowerIndex;
    private byte[] _lastColor = { 0, 229, 255 };
    // v0.9.13: status keys are event driven (StateWatch); this is only a slow safety net (was 1 s polling)
    private readonly DispatcherTimer _indicatorTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private StateWatch? _watch;
    private int _watchQueued;
    private readonly System.Collections.Generic.Dictionary<int, bool> _indicators = new();
    /// <summary>2.0.0-beta1.4 (ui review #12, services #23): the status keys that are on, indexed by key id, rebuilt on
    /// the UI thread when one changes and published by reference, so the animation thread never reads the dictionary
    /// the UI thread writes.</summary>
    private volatile bool[]? _indicatorSnap;
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

    public MainWindow()
    {
        // 2.0: Legend only (Legend.xaml + Legend2.xaml); it must be loaded before the XAML resolves its keys.
        App.ApplyTheme();
        Motion.Enabled = !_settings.ReduceMotion && Motion.WindowsAnimations;   // v0.8.2 Reduce motion; 1.3: + Windows Animation effects
        InitializeComponent();
        ReduceMotionToggle.IsChecked = _settings.ReduceMotion;
        _fx = new AlienFx(_settings);
        _lastPowerIndex = _settings.BalancedPowerIndex;

        // G-key hook: always on since v0.8 (no off switch); "Detect key" lives in the gear menu.
        _gkey = new GKeyHook(_settings.GKeyVk, _settings.GKeyScan);
        _gkey.Pressed += () => Dispatcher.BeginInvoke(new Action(async () => await ToggleGModeAsync()));
        InputThread.Invoke(() => _gkey.Install());   // 1.3.0-beta.1: own thread (review #4)
        UpdateGKeyText();
        F2PresetsToggle.IsChecked = _settings.F2Presets;
        F3OpensToggle.IsChecked = _settings.F3Opens;
        F4OverlayToggle.IsChecked = _settings.F4Overlay;
        InitOverlay();   // beta1.3-ov
        TaskbarToggle.IsChecked = _settings.ShowInTaskbar;
        ShowInTaskbar = _settings.ShowInTaskbar;
        _macro.LightsHotkey += () => Dispatcher.BeginInvoke(new Action(async () => await CycleLightsAsync()));   // 1.3.1 Ctrl+Alt+F
        _macro.Pressed += k =>
        {
            if (k == 0) Dispatcher.BeginInvoke(new Action(async () => await CyclePresetAsync()));
            else if (k == 1 && _settings.F3Opens)   // v0.9.16 F3 opens / hides the app (2.0: one view, no compact mode)
                Dispatcher.BeginInvoke(new Action(() => ((App)Application.Current).ToggleMain()));
            else if (k == 2)   // 2.0: F4 is reserved for the overlay (beta 2)
            {
                // beta1.3-ov ov.15: Shift + F4 cycles the levels; read the keys now, while they're still held
                var (shift, f4) = OverlayKeys();
                Dispatcher.BeginInvoke(new Action(() => OnF4Pressed(shift, f4)));
            }
        };
        // beta2 (Amine): plain Shift + F4 cycles the levels too (Shift + Fn+F4 comes through the macro report above)
        _macro.OverlayHotkey += () => Dispatcher.BeginInvoke(new Action(() => OnF4Pressed(shift: true)));
        InputThread.Invoke(() => _macro.Install());
        bool f4 = _settings.F4Overlay;
        InputThread.Invoke(() => _macro.SetOverlayHotkey(f4));
        FanHint.Text = "BIOS";
        GpuHint.Text = "OPTIMUS · 3070 Ti";
        InitTabs();   // 2.0: tab pages, Home preset tiles, Home G-Mode card

        foreach (var id in new[] { _settings.GModeKey, _settings.TouchpadKey, _settings.SpeakerMuteKey, _settings.MicMuteKey, _settings.WinLockKey })
            _indicators[id] = false;
        if (_settings.LastColor is { Length: 3 } saved) _lastColor = saved;
        ApplyAccent(_lastColor);
        SelectSwatch(_lastColor);
        BrightnessSlider.Value = HomeBrightnessSlider.Value = Math.Round(_settings.Brightness * 100 / 255.0);
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
                // 1.2.0-beta.5 (Amine: spamming F3/F4 froze the PC for a moment): every open ran alienfan-cli twice
                // plus the AWCC/BIOS ACPI reads, and quick open/close stacked them up. Now one refresh at a time, and
                // the CLI/BIOS reads are skipped when the last ones are under 20 s old (sensors still read every open).
                bool stale = (DateTime.UtcNow - _stateReadUtc).TotalSeconds > 20;
                // 2.0.0-beta1.4 (ui review #7): the GPU read (a Win32_PnPEntity WMI query) runs on open only when the last
                // one is stale (it also tells the power meter about iGPU only); otherwise when Tuning shows it
                if (stale) _ = RefreshGpuHintAsync(); else RefreshGpuIfShown();
                await RefreshStateAsync(sensorsOnly: !stale);
                if (!IsVisible || gen != _showGen) return;
                UpdatePolling();   // 2.0: the timers run only for a page that shows sensors (Home, Tuning)
            }
            else
            {
                // window hidden: no sensor or power polling at all (the tray idles at zero)
                UpdatePolling(); CloseSettings();   // beta1.3-ov: stops both unless the overlay shows sensors
                // 2.0.0-beta1.2 (Amine): every open (F3, tray icon, tray menu) starts on Home, never the last tab
                ShowPage(HomePage);
                // Keep the learned rest-of-laptop power for the AC estimate (saved only when it moved).
                if (Math.Abs(_settings.RestOfSystemW - _power.RestW) >= 1) { _settings.RestOfSystemW = Math.Round(_power.RestW, 1); _settings.Save(); }
            }
        };

        StateChanged += async (_, _) =>
        {
            // 2.0.0-beta1.3 (ui review #2): minimized = no sensor polling (SensorsShown is false), restored = polling again
            if (WindowState == WindowState.Minimized) { CloseSettings(); ShowPage(HomePage); UpdatePolling(); }
            else
            {
                if (WindowState == WindowState.Normal && _minSlid)
                {
                    _minSlid = false;   // v0.8.9: restored from a slid minimize: rise back up from below the screen
                    _showGen++;
                    _hiding = false;
                    if (Motion.Enabled) SlideIn(); else SnapToCorner();
                }
                int gen = _showGen;
                if (Motion.Enabled) await Task.Delay(OpenMs + 40);   // like an open: reads wait until the slide has played
                if (gen != _showGen || WindowState == WindowState.Minimized) return;
                UpdatePolling();
                if (SensorsShown) await RefreshSensorsAsync();
            }
        };

        // 2.0.0-beta1.4: the window has a fixed size (no SizeChanged snap, a compact-mode leftover); FitToWorkArea sizes
        // it for short screens and snaps it. Work area, display and DPI changes re-fit it.
        SystemParameters.StaticPropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.BeginInvoke(new Action(() => { FitToWorkArea(); SnapToCorner(); }));
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => { FitToWorkArea(); SnapToCorner(); }));
        FitToWorkArea();

        UpdateProfileChip(ShownProfile);
        // v1.0.0-rc1: informational version keeps the -rc suffix; drop the +commit the SDK appends.
        var ver = (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MainWindow).Assembly)?.InformationalVersion
                   ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1").Split('+')[0];
        VersionText.Text = "v" + ver;
        AboutTitle.Text = $"Alien Controls {ver}";
        AboutText.Text = "Fans, power and lighting by alienfx-tools (T-Troll, MIT). The overlay (beta 2) will use PresentMon (Intel, MIT). Not made by Dell or Alienware. Built with AI help, use at your own risk.";
        _ = Dispatcher.InvokeAsync(async () =>   // 1.3.0-beta.1: off the UI thread (review #4)
        {
            // 2.0.0-beta1.4: the check also moves an old task to the Program Files copy (or turns it off) and says why
            var r = await Startup.CheckAsync();
            StartupToggle.IsChecked = r.Enabled;
            if (r.Status is not null) Status(r.Status);
        });
        InitOc();   // v0.9.7 GPU overclock (G-Mode on AC only)
        InitAuto(); // v0.9.11 auto preset + toasts
        InitKbdTimeout();   // 1.3.0-beta.1
        SyncZoneTiles();   // 2.0: the 1.3.5 Lights menu is inline on the Lights tab
        InitSettingsPage();   // 1.3.0-beta.2
        _ = Task.Run(() => FolderSafety.UserWritable(AppContext.BaseDirectory)).ContinueWith(t =>
        {
            if (t.Result) Dispatcher.BeginInvoke(new Action(() =>
                Status("Heads-up: this folder can be changed without admin rights. Run on startup uses a safe copy in Program Files.")));
        });

        if (!_fan.IsAvailable || !_fx.IsAvailable)
            Status((_fan.IsAvailable ? "alienfx-cli.exe" : _fx.IsAvailable ? "alienfan-cli.exe" : "alienfan-cli.exe and alienfx-cli.exe")
                   + " missing. Put it next to AlienControl.exe (or in a tools folder there), then restart the app.");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The X button hides to tray; Quit really exits.
        if (!_allowClose) { e.Cancel = true; HideToTray(); }
        base.OnClosing(e);
    }

    private async void Startup_Click(object sender, RoutedEventArgs e)
    {
        // 2.0: Run on startup lives in the footer only
        bool want = StartupToggle.IsChecked == true;
        StartupToggle.IsEnabled = false;
        bool ok = await Startup.SetEnabledAsync(want);
        StartupToggle.IsEnabled = true;
        if (!ok)
        {
            StartupToggle.IsChecked = !want;
            Status(want ? "Couldn't set up Run on startup (copy to Program Files failed, details in crash.log)."
                        : "Couldn't change the startup task.");   // 2.0.0-beta1.4
        }
        else Status(want ? "Run on startup on. It starts the copy in Program Files." : "Run on startup off. The Program Files copy is removed.");
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).Quit();

    // ---------- helpers ----------

    /// <summary>
    /// App accent follows the keyboard / ring color. Black (lights off) keeps the Legend cyan #00E5FF.
    /// Set at App.Resources level.
    /// 2.0.0-beta1.4 (ui review #9): returns at once when nothing changed (it rebuilt 8 brushes, restyled the tray menu
    /// and walked the whole visual tree on every brightness commit, zone tap and resume).
    /// 2.0.0-beta1.4 (Amine): it also publishes the selected-fill tints (AccentSelBg, AccentPillBg), a dark blend of the
    /// lighting colour over the card, so selected tiles, pills, toggles and primary buttons follow the lighting colour
    /// instead of a fixed teal. White (or any grey) gets a weaker, neutral grey tint so text stays readable; Spectrum /
    /// Rainbow tint with the default cyan.
    /// </summary>
    private void ApplyAccent(byte[] c)
    {
        var cyan = Color.FromRgb(0, 229, 255);
        var col = (c[0] + c[1] + c[2]) < 60 ? cyan : Color.FromRgb(c[0], c[1], c[2]);
        var tint = _settings.LastEffect >= 100 ? cyan : col;
        if (_accentApplied && col == AccentColor && tint == _tintColor) return;
        _accentApplied = true;
        _tintColor = tint;
        var res = Application.Current.Resources;
        Color A(byte a) => Color.FromArgb(a, col.R, col.G, col.B);
        res["AccentBrush"] = Freeze(new SolidColorBrush(col));
        res["AccentBorder"] = Freeze(new SolidColorBrush(A(0x80)));
        res["AccentFaint"] = Freeze(new SolidColorBrush(A(0x2E)));
        res["AccentGlow"] = Freeze(new SolidColorBrush(A(0x55)));
        res["AccentCheckedBg"] = Freeze(new LinearGradientBrush(A(0x17), A(0x05), 90));
        var (sel, pill) = AccentTints(tint);
        res["AccentSelBg"] = Freeze(new SolidColorBrush(sel));
        res["AccentPillBg"] = Freeze(new SolidColorBrush(pill));
        var top = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        top.GradientStops.Add(new GradientStop(A(0), 0));
        top.GradientStops.Add(new GradientStop(col, 0.18));
        top.GradientStops.Add(new GradientStop(col, 0.5));
        top.GradientStops.Add(new GradientStop(Color.FromRgb(0x7C, 0x4D, 0xFF), 0.82));
        top.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x7C, 0x4D, 0xFF), 1));
        res["TopLineBrush"] = Freeze(top);
        // 2.0.0-beta1.4: WPF-UI's ApplicationAccentColorManager call is gone with the package (nothing used its accent)
        AccentColor = col;
        (Application.Current as App)?.UpdateTrayMenu(col);   // v0.8.7: tray menu follows the accent
        // v0.8.10: line icons bind Stroke to their inherited TextElement.Foreground; WPF updated them one click late
        // (the old accent stayed until the next change). Re-read the bindings and repaint once, after the resource swap.
        Dispatcher.BeginInvoke(new Action(() => RefreshIcons(this)), DispatcherPriority.Loaded);
    }

    private bool _accentApplied;
    private Color _tintColor;

    /// <summary>2.0.0-beta1.4 (Amine): selected fills. Sel = tiles, toggles, primary buttons, chip (about 16% of the
    /// colour over the card #0E1013); Pill = the selected item of a pill track (about 22% over #0B0D10). A colour with
    /// little saturation (white) gets about 60% of that strength, a neutral grey.</summary>
    internal static (Color Sel, Color Pill) AccentTints(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        double sat = max == 0 ? 0 : (max - min) / (double)max;
        double k = sat < 0.25 ? 0.6 : 1.0;
        static Color Mix(Color bg, Color fg, double t) => Color.FromRgb(
            (byte)Math.Round(bg.R + (fg.R - bg.R) * t), (byte)Math.Round(bg.G + (fg.G - bg.G) * t), (byte)Math.Round(bg.B + (fg.B - bg.B) * t));
        return (Mix(Color.FromRgb(0x0E, 0x10, 0x13), c, 0.16 * k), Mix(Color.FromRgb(0x0B, 0x0D, 0x10), c, 0.22 * k));
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

    /// <summary>v0.8.7: current accent, read by the tray menu.</summary>
    public Color AccentColor { get; private set; } = Color.FromRgb(0, 229, 255);

    /// <summary>v0.8.7: visible, not minimized and not already sliding away (tray left click then hides it).</summary>
    public bool IsShownOpen => IsVisible && WindowState != WindowState.Minimized && !_hiding;

    // ---------- hints ----------

    /// <summary>Section header hints, in Legend caps. 2.0.0-beta1.4: Legend only, so the Fluent text / Legend short form
    /// pair is gone; a hint with its own casing ("240 Hz") is set directly.</summary>
    private static void SetHint(TextBlock tb, string text) => tb.Text = text.ToUpperInvariant();

    private void ReduceMotion_Click(object sender, RoutedEventArgs e)
    {
        _settings.ReduceMotion = ReduceMotionToggle.IsChecked == true;
        Motion.Enabled = !_settings.ReduceMotion && Motion.WindowsAnimations;
        _settings.Save();
        if (_settings.ReduceMotion) Motion.Set(PresetButton, OpacityProperty, 1);
    }

    /// <summary>Checks the matching swatch in both rows (Home and Lights); custom colors check the rainbow swatch.</summary>
    private void SelectSwatch(byte[] c)
    {
        string tag = string.Join(",", c);
        foreach (var (panel, custom) in new[] { (LightsSwatches, CustomSwatch), (HomeSwatches, HomeCustomSwatch) })
        {
            var all = panel.Children.OfType<RadioButton>().ToList();
            var match = all.FirstOrDefault(rb => (rb.Tag as string) == tag);
            if (match is not null) match.IsChecked = true;
            else if (c.All(v => v == 0)) foreach (var rb in all) rb.IsChecked = false;
            else custom.IsChecked = true;
        }
        ColorHint.Text = SwatchName(tag);
    }

    private static string SwatchName(string tag) => tag switch
    {
        "0,229,255" => "CYAN", "124,77,255" => "PURPLE", "255,77,106" => "RED", "60,255,122" => "GREEN", "255,255,255" => "WHITE",
        "0,0,0" => "", _ => "CUSTOM",
    };

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
        var pick = await ShowGpuDialog("Reset settings?",
            "Toggles, colors, effect, G-key, OC values and presets go back to defaults, then the app restarts. "
            + "Run on startup, the BIOS and the GPU mode stay as they are. GPU OC goes back to +0.",
            null, null, ("cancel", "Cancel", false), ("reset", "Reset", true));
        if (pick != "reset") return;
        ((App)Application.Current).RestartFresh();
    }

    /// <summary>2.0.0-beta1.4: the status line for an exception App caught and handled.</summary>
    public void ShowError(string text) => Status(text);

    /// <summary>Status line above the footer (last action). 2.0: one line, cut with an ellipsis; the full text is the
    /// tooltip. Hidden behind the settings page hint while Settings is open.</summary>
    private void Status(string text)
    {
        StatusText.Text = text;
        StatusText.ToolTip = string.IsNullOrWhiteSpace(text) ? null : text;
        StatusText.Visibility = SettingsShown ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- RAM clean (click = quick, right-click = deep) ----------

    private bool _cleaning;
    private void RamCard_LeftClick(object sender, MouseButtonEventArgs e) => _ = CleanRamAsync(deep: false);
    private void RamCard_RightClick(object sender, MouseButtonEventArgs e) { e.Handled = true; _ = CleanRamAsync(deep: true); }

    private async Task CleanRamAsync(bool deep)
    {
        if (_cleaning) return;
        _cleaning = true;
        CleanText.Text = deep ? "DEEP CLEANING…" : "CLEANING…";
        try
        {
            var (freed, error) = await MemoryCleaner.CleanAsync(deep);
            if (error is not null) Status(error);
            CleanText.Text = error is not null ? "CLEAN FAILED" : $"FREED {freed:0.0} GB";
            if (SystemStats.Ram() is { } ram) SetMem(RamText, RamTextUnit, RamBar, ram.Used, ram.Total);
            await Task.Delay(4000);
        }
        finally
        {
            CleanText.SetResourceReference(TextBlock.TextProperty, "Str.Clean");
            _cleaning = false;
        }
    }

}
