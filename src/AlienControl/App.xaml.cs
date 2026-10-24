using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using AlienControl.Services;
using WinForms = System.Windows.Forms;

namespace AlienControl;

public partial class App : Application
{
    private WinForms.NotifyIcon? _tray;
    private MainWindow? _window;
    private readonly Dictionary<TrayProfile, WinForms.ToolStripMenuItem> _profileItems = new();
    private Icon? _baseIcon;
    private System.Threading.Mutex? _single;
    private System.Threading.EventWaitHandle? _showEvent;
    private IntPtr _tintHandle;
    private WinForms.ContextMenuStrip? _menu;
    private Font? _menuFont;
    private readonly TrayMenuStyle _menuStyle = new();
    private readonly List<(WinForms.ToolStripItem Item, string Text)> _menuTexts = new();

    /// <summary>2.0: Legend is the only theme. Loads the generated Legend dictionary plus the 2.0 styles (Legend2.xaml)
    /// into App.Resources once. The old "Theme" setting is ignored (Fluent was removed in 2.0).</summary>
    public static void ApplyTheme()
    {
        var md = Current.Resources.MergedDictionaries;
        if (md.Any(d => d.Source?.OriginalString.Contains("/Themes/", StringComparison.OrdinalIgnoreCase) == true)) return;
        md.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Legend.xaml", UriKind.Absolute) });
        md.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Legend2.xaml", UriKind.Absolute) });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 1.2.0-beta.4: local crash log (%AppData%\AlienControl\crash.log), never sent anywhere
        // 2.0.0-beta1.3 (services review #2): the two fatal ones also put the GPU OC back to +0 and clear the CPU cap
        // (best effort, bounded), so a crash can't leave a kept overclock running on battery until reboot
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { CrashLog("Domain", a.ExceptionObject as Exception); CrashCleanup(); };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, a) => { CrashLog("Task", a.Exception); a.SetObserved(); };
        // 1.3.0-beta.1 (review #1): single instance. A second copy wakes the first one's window and exits, so there's
        // never two tray icons / two G-key hooks (F1 toggling twice, F2 skipping a preset).
        _single = new System.Threading.Mutex(true, @"Local\AlienControls.SingleInstance", out bool first);
        _showEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\AlienControls.Show");
        if (!first)
        {
            // Reset settings starts a new copy while the old one is exiting: wait for it to let go.
            bool restarted = e.Args.Contains("--restarted", StringComparer.OrdinalIgnoreCase);
            try { first = restarted && _single.WaitOne(8000); } catch (System.Threading.AbandonedMutexException) { first = true; }
            if (!first)
            {
                if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) _showEvent.Set();
                Shutdown();
                return;
            }
        }
        System.Threading.ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => ShowMain())), null, -1, false);   // kernel wait, no polling

        _window = new MainWindow();
        _window.ProfileChanged += p => Dispatcher.BeginInvoke(new Action(() => ShowProfile(p)));
        CreateTray();
        ShowProfile(_window.CurrentProfile);

        // "--tray" is passed by the logon task: start hidden, zero polling.
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
            ShowMain();
    }

    private static void CrashLog(string where, Exception? ex) => Log.Error(where, ex);

    private readonly Queue<long> _uiErrors = new();

    /// <summary>2.0.0-beta1.4 (ui review #6): an exception that escapes a UI handler (every async void click, timer and
    /// BeginInvoke lambda ends up here) used to kill the app, skipping Quit's cleanup. A non-fatal one is now logged,
    /// shown in the status line and handled, so the app keeps running. A fatal one (out of memory, corrupted state) or
    /// a burst of more than 5 in 10 s still ends the app, after the bounded crash cleanup (OC +0, CPU cap cleared).</summary>
    private void OnUiException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs a)
    {
        CrashLog("UI", a.Exception);
        long now = Environment.TickCount64;
        _uiErrors.Enqueue(now);
        while (_uiErrors.Count > 0 && now - _uiErrors.Peek() > 10_000) _uiErrors.Dequeue();
        bool fatal = a.Exception is OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException
            or InvalidProgramException or System.Runtime.InteropServices.SEHException or TypeInitializationException;
        if (!fatal && _uiErrors.Count <= 5 && !_quitting)
        {
            a.Handled = true;
            try { _window?.ShowError("Something went wrong (details in crash.log). " + a.Exception.Message); }
            catch (Exception ex) { Log.Warn("UI error status", ex); }
            return;
        }
        CrashCleanup();
    }

    private void CrashCleanup()
    {
        try { _window?.EmergencyExitCleanup(3000); } catch (Exception ex) { Log.Warn("CrashCleanup", ex); }
        // 2.0.0-beta1.4: also one last write of settings a locked settings.json held back
        try { Settings.FlushPending(); } catch (Exception ex) { Log.Warn("CrashCleanup.Settings", ex); }
    }

    /// <summary>2.0.0-beta1.3 (services review #2): logoff, user switch to sign-out, restart or shutdown. Quit's async
    /// cleanup can't run here, so its synchronous parts do: OC +0 and the CPU cap cleared (bounded), then the normal
    /// close (hooks, timers, tray icon). The G-Mode boost restore (an alienfan-cli run) is skipped.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel || _quitting) return;
        _quitting = true;
        Log.Info("Session", $"Windows is ending the session ({e.ReasonSessionEnding})");
        try
        {
            _window?.EmergencyExitCleanup(4000);
            if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            _window?.AllowClose();
        }
        catch (Exception ex) { Log.Warn("SessionEnding", ex); }
    }

    private void CreateTray()
    {
        // v0.8.7: themed menu (TrayMenu.cs). 1.3.1 (Amine): no check column; each preset shows its own icon (accent on
        // the selected preset, muted on the others) and its name in the preset's color; rounded, app font, both themes.
        var menu = new WinForms.ContextMenuStrip { ShowCheckMargin = false, ShowImageMargin = true, DropShadowEnabled = true };
        menu.Renderer = new TrayMenuRenderer(_menuStyle);
        int iconPx = TrayIconPx();
        menu.ImageScalingSize = new System.Drawing.Size(iconPx, iconPx);
        WinForms.ToolStripMenuItem Add(string text, EventHandler click, Image? img = null)
        {
            var it = new WinForms.ToolStripMenuItem(text, img, click);
            menu.Items.Add(it);
            _menuTexts.Add((it, text));
            return it;
        }
        Add("Open Alien Controls", (_, _) => ShowMain());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        foreach (TrayProfile p in Enum.GetValues(typeof(TrayProfile)))
        {
            if (p == TrayProfile.GMode) continue;   // v0.9.8: display-only, G-Mode lives on F1 / the window
            var profile = p;
            var item = Add(Profiles.Label(p), async (_, _) => { if (_window is not null) await _window.ApplyProfileAsync(profile); });
            item.Tag = Profiles.Tint(p);   // name color (TrayMenuRenderer)
            item.ToolTipText = Profiles.Describe(p);
            _profileItems[p] = item;
        }
        menu.Items.Add(new WinForms.ToolStripSeparator());
        Add("Quit", (_, _) => Quit());
        menu.Opening += (_, _) =>
        {
            _menuStyle.ApplyCorners(menu.Handle);
            // v0.9.11: Battery comes on by itself on unplug (Auto preset), so it's greyed on AC
            if (_window is not null && _profileItems.TryGetValue(TrayProfile.Battery, out var bat)) bat.Enabled = !_window.BatteryLocked;
        };
        _menu = menu;
        if (_window is not null) UpdateTrayMenu(_window.AccentColor);

        _baseIcon = LoadTrayIcon();
        _tray = new WinForms.NotifyIcon
        {
            Text = "Alien Controls",
            Icon = _baseIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        // v0.8.7: left click toggles: opens the window, or slides it back to the tray when it's already open.
        _tray.MouseClick += (_, a) =>
        {
            if (a.Button != WinForms.MouseButtons.Left || _window is null) return;
            if (_window.IsShownOpen) _window.HideToTray();
            else ShowMain();
        };
    }

    /// <summary>v0.9.16 F3: open the window; open and focused = slide back to the tray; open behind a game = bring it up.
    /// 2.0: one view (compact mode is gone), so F3 is a plain open / hide toggle, on the settings page too.</summary>
    public void ToggleMain()
    {
        if (_window is null) return;
        if (_window.IsShownOpen && _window.IsActive) _window.HideToTray();
        else if (_window.IsShownOpen) { _window.Activate(); ForceForeground(); }
        else ShowMain();
    }

    /// <summary>v0.8.7: restyles the tray menu for the accent (called by MainWindow when the accent changes).
    /// 2.0.0-beta1.4: Legend only, so the theme flag is gone.</summary>
    public void UpdateTrayMenu(System.Windows.Media.Color accent)
    {
        _menuStyle.Set(Color.FromArgb(accent.R, accent.G, accent.B));
        if (_menu is null) return;
        var old = _menuFont;
        _menuFont = _menuStyle.MakeFont();
        _menu.Font = _menuFont;
        old?.Dispose();   // only fonts we made; never the shared default menu font
        var pad = new WinForms.Padding(0, 3, 0, 3);
        foreach (var (item, text) in _menuTexts)
        {
            item.Text = text;   // 1.3.1: the app's text font in normal case (was Legend caps)
            item.Padding = pad;
        }
        _menu.BackColor = _menuStyle.Back;
        RefreshProfileIcons();
        _menu.Invalidate();
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private TrayProfile? _shownProfile;

    /// <summary>Icon size in device pixels: 16 px at the system DPI (20 px at 125%).</summary>
    private static int TrayIconPx()
    {
        try { using var g = Graphics.FromHwnd(IntPtr.Zero); return (int)Math.Round(16 * g.DpiX / 96f); }
        catch { return 16; }
    }

    /// <summary>1.3.1: the preset icons of the current theme, accent on the selected preset and muted on the others.</summary>
    private void RefreshProfileIcons()
    {
        if (_menu is null) return;
        var accent = System.Windows.Media.Color.FromRgb(_menuStyle.Accent.R, _menuStyle.Accent.G, _menuStyle.Accent.B);
        var muted = (TryFindResource("TextMuted") as System.Windows.Media.SolidColorBrush)?.Color ?? System.Windows.Media.Color.FromRgb(0x6E, 0x7A, 0x86);
        int px = _menu.ImageScalingSize.Width;
        foreach (var (key, item) in _profileItems)
        {
            var old = item.Image;
            item.Image = RenderIcon(Profiles.IconKey(key), key == _shownProfile ? accent : muted, px);
            old?.Dispose();
        }
    }

    /// <summary>Draws a theme icon resource (WPF vector) into a bitmap for the WinForms menu.</summary>
    private Bitmap? RenderIcon(string key, System.Windows.Media.Color color, int px)
    {
        try
        {
            if (TryFindResource(key) is not FrameworkElement icon) return null;
            var brush = new System.Windows.Media.SolidColorBrush(color);
            brush.Freeze();
            var host = new System.Windows.Controls.Border { Width = px, Height = px, Padding = new Thickness(px * 0.06), Child = icon };
            System.Windows.Documents.TextElement.SetForeground(host, brush);
            host.Measure(new System.Windows.Size(px, px));
            host.Arrange(new Rect(0, 0, px, px));
            host.UpdateLayout();
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(px, px, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(host);
            var bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, px, px), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try { rtb.CopyPixels(Int32Rect.Empty, data.Scan0, data.Stride * px, data.Stride); }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }
        catch (Exception ex) { Log.Warn("TrayMenu.Icon", ex); return null; }
    }

    /// <summary>Checks the profile in the tray menu and tints the tray icon with its color.</summary>
    private void ShowProfile(TrayProfile p)
    {
        // 2.0.0-beta1.4 (ui review #10): nothing to redraw when the shown profile is the same
        if (_shownProfile == p && _tintHandle != IntPtr.Zero) return;
        _shownProfile = p;
        RefreshProfileIcons();
        if (_tray is null || _baseIcon is null) return;
        _tray.Text = $"Alien Controls · {Profiles.Label(p)}";
        var tint = Profiles.Tint(p);
        try
        {
            using var src = _baseIcon.ToBitmap();
            using var bmp = new Bitmap(src.Width, src.Height);
            for (int y = 0; y < src.Height; y++)
                for (int x = 0; x < src.Width; x++)
                {
                    var c = src.GetPixel(x, y);
                    // Multiply: the white Alienware head takes the profile color, transparency is kept.
                    bmp.SetPixel(x, y, Color.FromArgb(c.A, c.R * tint.R / 255, c.G * tint.G / 255, c.B * tint.B / 255));
                }
            var h = bmp.GetHicon();
            var old = _tintHandle;
            _tray.Icon = Icon.FromHandle(h);
            _tintHandle = h;
            if (old != IntPtr.Zero) DestroyIcon(old);
        }
        catch { _tray.Icon = _baseIcon; }
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var res = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res is not null) return new Icon(res.Stream, WinForms.SystemInformation.SmallIconSize);
        }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("App.xaml.cs:197", __e); }
        return Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? SystemIcons.Application;
    }

    public void ShowMain()
    {
        if (_window is null) return;
        _window.PrepareShow();   // v0.8.2: cancels a hide animation that is still running
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.SnapToCorner();
        _window.Topmost = true;
        _window.Activate();
        ForceForeground();
    }

    /// <summary>v0.9.16: F3 fires while a game owns the foreground, and Windows blocks plain SetForegroundWindow from a
    /// background app. Briefly attaching to the foreground thread's input lets the window take focus.</summary>
    private void ForceForeground()
    {
        if (_window is null) return;
        var h = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        var fg = GetForegroundWindow();
        if (h == IntPtr.Zero || fg == h) return;
        uint fgThread = GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try { SetForegroundWindow(h); BringWindowToTop(h); }
        finally { if (attached) AttachThreadInput(me, fgThread, false); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);

    private bool _quitting;

    public async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        try { if (_window is not null) await _window.PrepareExitAsync(); }
        catch (Exception ex) { Log.Warn("PrepareExit", ex); }
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        if (_tintHandle != IntPtr.Zero) DestroyIcon(_tintHandle);
        _window?.AllowClose();
        Shutdown();
    }

    /// <summary>v0.9.15 Reset settings: clean exit (OC +0, boost restored), delete settings.json, start again with the window open.
    /// 2.0.0-beta1.3 (ui review #3): awaits the same exit cleanup as Quit (CPU cap cleared, boost restored), without the
    /// iGPU prompt, and no longer blocks the UI thread.</summary>
    public async void RestartFresh()
    {
        if (_quitting) return;
        _quitting = true;
        try { if (_window is not null) await _window.PrepareExitAsync(promptIgpu: false); }
        catch (Exception ex) { Log.Warn("PrepareExit", ex); }
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        if (_tintHandle != IntPtr.Zero) { DestroyIcon(_tintHandle); _tintHandle = IntPtr.Zero; }
        _window?.AllowClose();
        Settings.Delete();   // after AllowClose, which can still save
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--restarted") { UseShellExecute = false });
        }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("App.xaml.cs:262", __e); /* user can start it again */ }
        Shutdown();
    }
}
