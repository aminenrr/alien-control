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
    private IntPtr _tintHandle;

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

        _window = new MainWindow();
        _window.ProfileChanged += p => Dispatcher.BeginInvoke(new Action(() => ShowProfile(p)));
        CreateTray();
        ShowProfile(_window.CurrentProfile);

        // "--tray" is passed by the logon task: start hidden, zero polling.
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
            ShowMain();
    }

    private void CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Alien Controls", null, (_, _) => ShowMain());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        foreach (TrayProfile p in Enum.GetValues(typeof(TrayProfile)))
        {
            var item = new WinForms.ToolStripMenuItem(Profiles.Label(p)) { ToolTipText = Profiles.Describe(p) };
            var profile = p;
            item.Click += async (_, _) => { if (_window is not null) await _window.ApplyProfileAsync(profile); };
            menu.Items.Add(item);
            _profileItems[p] = item;
        }
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _baseIcon = LoadTrayIcon();
        _tray = new WinForms.NotifyIcon
        {
            Text = "Alien Controls",
            Icon = _baseIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.MouseClick += (_, a) => { if (a.Button == WinForms.MouseButtons.Left) ShowMain(); };
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
        catch { }
        return Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? SystemIcons.Application;
    }

    public void ShowMain()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.SnapToCorner();
        _window.Topmost = true;
        _window.Activate();
    }

    public void Quit()
    {
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        if (_tintHandle != IntPtr.Zero) DestroyIcon(_tintHandle);
        _window?.AllowClose();
        Shutdown();
    }
}
