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

    /// <summary>Loads only the chosen theme dictionary into App.Resources, replacing the previous one.</summary>
    public static void ApplyTheme(string name)
    {
        var md = Current.Resources.MergedDictionaries;
        var file = name == "Fluent" ? "Fluent" : "Legend";
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{file}.xaml", UriKind.Absolute) };
        var old = md.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Themes/", StringComparison.OrdinalIgnoreCase) == true);
        if (old is null) md.Add(dict);
        else md[md.IndexOf(old)] = dict;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 1.2.0-beta.4: local crash log (%AppData%\AlienControl\crash.log), never sent anywhere
        DispatcherUnhandledException += (_, a) => CrashLog("UI", a.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, a) => CrashLog("Domain", a.ExceptionObject as Exception);
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

    private void CreateTray()
    {
        // v0.8.7: themed menu (TrayMenu.cs): accent text, Legend / Fluent palette, check column + profile tint dot.
        var menu = new WinForms.ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = true, DropShadowEnabled = true };
        menu.Renderer = new TrayMenuRenderer(_menuStyle);
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
            var item = Add(Profiles.Label(p), async (_, _) => { if (_window is not null) await _window.ApplyProfileAsync(profile); },
                           TrayMenuRenderer.Dot(Profiles.Tint(p)));
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
        if (_window is not null) UpdateTrayMenu(_window.IsLegendTheme, _window.AccentColor);

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
            else ShowMain(full: false);   // v1.1: the tray always opens the compact view
        };
    }

    /// <summary>v0.9.16 F3: open the window; open and focused = slide back to the tray; open behind a game = bring it up.
    /// v1.1: F3 = compact, F4 = full. Pressing the key of the view already shown (and focused) hides it; the other key
    /// switches the open window to its view.</summary>
    public void ToggleMain(bool full)
    {
        if (_window is null) return;
        bool sameView = _window.IsCompact == !full;
        if (_window.IsShownOpen && _window.IsActive && sameView) _window.HideToTray();
        else if (_window.IsShownOpen)
        {
            _window.SwitchView(!full);   // 1.2.0-beta.3: dip-and-rise switch
            if (!_window.IsActive) { _window.Activate(); ForceForeground(); }   // already focused: skip the foreground calls
        }
        else ShowMain(full);
    }

    /// <summary>v0.8.7: restyles the tray menu for the theme and accent (called by MainWindow on every change).</summary>
    public void UpdateTrayMenu(bool legend, System.Windows.Media.Color accent)
    {
        _menuStyle.Set(legend, Color.FromArgb(accent.R, accent.G, accent.B));
        if (_menu is null) return;
        var old = _menuFont;
        _menuFont = _menuStyle.MakeFont();
        _menu.Font = _menuFont;
        old?.Dispose();   // only fonts we made; never the shared default menu font
        var pad = legend ? new WinForms.Padding(0, 3, 0, 3) : new WinForms.Padding(0, 5, 0, 5);
        foreach (var (item, text) in _menuTexts)
        {
            item.Text = legend ? text.ToUpperInvariant() : text;   // Legend caps, like the header chip
            item.Padding = pad;
        }
        _menu.BackColor = _menuStyle.Back;
        _menu.Invalidate();
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Checks the profile in the tray menu and tints the tray icon with its color.</summary>
    private void ShowProfile(TrayProfile p)
    {
        foreach (var (key, item) in _profileItems) item.Checked = key == p;
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

    public void ShowMain(bool full = false)
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.SetCompact(!full);   // v1.1: every open starts compact unless F4
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

    /// <summary>v0.9.15 Reset settings: clean exit (OC +0, boost restored), delete settings.json, start again with the window open.</summary>
    public void RestartFresh()
    {
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
