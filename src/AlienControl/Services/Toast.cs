using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace AlienControl.Services;

/// <summary>
/// v0.9.11: small themed notification, top-right of the main screen (16 px in), ~1.6 s. Never takes focus and is
/// click-through. Created per show and closed after, so it costs nothing when idle. Only for F1, F2 and plug/unplug;
/// window and tray clicks stay silent. Mockups: toast2 / toast3 (2026-10-15).
/// </summary>
public static class Toast
{
    private const int Inset = 16, HoldMs = 1600, InMs = 140, OutMs = 260;
    private static Window? _w;
    private static bool _hooked;
    private static (bool legend, TrayProfile p, string sub, bool cycle, bool animate, int hold)? _last;
    private static DispatcherTimer? _timer;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <param name="cycle">Light the F2 cycle pills (Quiet / Balanced / Performance).</param>
    public static void Show(bool legend, TrayProfile p, string sub, bool cycle, bool animate, int holdMs = HoldMs)
    {
        var app = Application.Current;
        if (app is null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(new Action(() => Show(legend, p, sub, cycle, animate, holdMs))); return; }

        Close();   // a new toast replaces the old one at once
        _last = (legend, p, sub, cycle, animate, holdMs);
        if (!_hooked)
        {
            // v0.9.12: unplugging can move the screen to the other GPU (Advanced Optimus) or change its rate; a toast that
            // is up when the display changes gets lost, so it's shown again once the display has settled.
            _hooked = true;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (_w is null || _last is not { } l) return;
                var mine = _w;
                await System.Threading.Tasks.Task.Delay(700);
                if (_w == mine) Show(l.legend, l.p, l.sub, l.cycle, false, 2000);
            }));
        }
        var t = Profiles.Tint(p);
        var tint = Color.FromRgb(t.R, t.G, t.B);
        var title = Profiles.Label(p);

        var ui = app.TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI");
        var mono = app.TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Consolas");

        var grid = new Grid { Width = 340 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var iconKey = p switch
        {
            TrayProfile.Battery => "Icon.Battery", TrayProfile.Quiet => "Icon.Quiet", TrayProfile.Balanced => "Icon.Balanced",
            TrayProfile.Performance => "Icon.Performance", TrayProfile.GMode => "Icon.GMode", _ => "Icon.Settings",
        };
        var icon = new ContentControl
        {
            Content = app.TryFindResource(iconKey), Width = 20, Height = 20, Foreground = B(tint),
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false,
        };
        TextElement.SetForeground(icon, B(tint));
        grid.Children.Add(icon);

        var head = new TextBlock
        {
            Text = legend ? title.ToUpperInvariant() : title,
            FontFamily = legend ? mono : ui, FontWeight = FontWeights.Bold, FontSize = legend ? 15 : 16,
            Foreground = legend ? B(tint) : Brushes.White, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(head, 1);
        grid.Children.Add(head);

        if (cycle)
        {
            var pills = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var q in new[] { TrayProfile.Quiet, TrayProfile.Balanced, TrayProfile.Performance })
            {
                var qt = Profiles.Tint(q);
                var qc = Color.FromRgb(qt.R, qt.G, qt.B);
                bool on = q == p;
                pills.Children.Add(new Border
                {
                    Width = 16, Height = 8, Margin = new Thickness(6, 0, 0, 0), CornerRadius = new CornerRadius(legend ? 1 : 4),
                    Background = on ? B(qc) : legend ? Brushes.Transparent : B(C("#3A3C42")),
                    BorderBrush = legend ? B(qc) : null, BorderThickness = new Thickness(legend ? 1 : 0),
                });
            }
            Grid.SetColumn(pills, 2);
            grid.Children.Add(pills);
        }

        var line = new TextBlock
        {
            Text = legend ? sub.ToUpperInvariant() : sub,
            FontFamily = legend ? mono : ui, FontSize = legend ? 10.5 : 12.5,
            Foreground = B(C(legend ? "#6B7682" : "#A0A0A6")), Margin = new Thickness(0, 10, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(line, 1);
        Grid.SetColumnSpan(line, 3);
        grid.Children.Add(line);

        var card = new Border
        {
            Child = grid,
            Padding = new Thickness(18, 14, 18, 14),
            Background = B(C(legend ? "#0E1115" : "#2A2B2F")),
            BorderBrush = B(C(legend ? "#222932" : "#3A3C42")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(legend ? 6 : 10),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.55, Color = Colors.Black },
        };
        UIElement root = card;
        if (legend)
        {
            // Legend: 3 px preset-colored bar on the left edge
            var host = new Grid();
            host.Children.Add(card);
            host.Children.Add(new Border
            {
                Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = B(tint),
                CornerRadius = new CornerRadius(6, 0, 0, 6),
            });
            root = host;
        }

        const int pad = 24;   // room for the shadow
        // v0.9.12: size it by hand. SizeToContent came out ~4x too tall on the laptop (125% scaling), stretching the rows.
        var content = new Border { Child = root, Padding = new Thickness(pad), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = content.DesiredSize;
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false, Focusable = false, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Manual, Width = Math.Ceiling(size.Width), Height = Math.Ceiling(size.Height),
            MinWidth = 0, MinHeight = 0, IsHitTestVisible = false, Content = content,
            Opacity = animate ? 0 : 1,
        };
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        w.Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            w.Left = wa.Right - w.Width - Inset + pad;
            w.Top = wa.Top + Inset - pad;
            if (animate) w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(InMs)));
        };
        // park off screen until Loaded positions it (no flash at 0,0)
        w.Left = -10000; w.Top = -10000;
        _w = w;
        w.Show();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(holdMs + InMs) };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            if (!animate || _w != w) { Close(); return; }
            var fade = new DoubleAnimation(w.Opacity, 0, TimeSpan.FromMilliseconds(OutMs));
            fade.Completed += (_, _) => { if (_w == w) Close(); };
            w.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        _timer.Start();
    }

    private static void Close()
    {
        _timer?.Stop(); _timer = null;
        var w = _w; _w = null;
        try { w?.Close(); } catch { }
    }
}

/// <summary>v0.9.11: soft notification sounds (embedded WAVs, played async from memory, no extra packages).</summary>
public static class Sounds
{
    public enum Kind { Tick, Unplug, Plug, GModeOn, GModeOff }

    [DllImport("winmm.dll")] private static extern bool PlaySound(IntPtr data, IntPtr mod, uint flags);
    private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;
    // unmanaged copies kept for the life of the app (async playback reads them after the call returns; ~100 KB total)
    private static readonly System.Collections.Generic.Dictionary<Kind, IntPtr> _buf = new();

    public static void Play(Kind k)
    {
        try
        {
            if (!_buf.TryGetValue(k, out var p))
            {
                var name = k switch
                {
                    Kind.Tick => "snd-tick.wav", Kind.Unplug => "snd-unplug.wav", Kind.Plug => "snd-plug.wav",
                    Kind.GModeOn => "snd-gmode-on.wav", _ => "snd-gmode-off.wav",
                };
                var res = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"));
                if (res is null) return;
                using var ms = new MemoryStream();
                res.Stream.CopyTo(ms);
                var bytes = ms.ToArray();
                p = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                _buf[k] = p;
            }
            PlaySound(p, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }
        catch { }
    }
}
