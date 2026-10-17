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
/// v1.2: themed notification card in the Dolby Controls style, top-right of the main screen (16 px in), ~1.6 s.
/// 44 px icon tile tinted in the preset's color, title + detail line, state on the right (F2 pills, G-Mode switch,
/// preset chip for plug / unplug). Never takes focus and is click-through. Created per show and closed after, so it
/// costs nothing when idle. Mockup: files/mockups/toasts-v2 (2026-10-17).
/// </summary>
public static class Toast
{
    public enum Right { None, Pills, SwitchOn, SwitchOff, Chip }

    /// <param name="Tint">Preset whose color tints the tile, bar and chip.</param>
    /// <param name="Lit">False draws the tile dim (G-Mode off).</param>
    public sealed record Info(TrayProfile Tint, string IconKey, string Title, string Sub, Right Right = Right.None, bool Lit = true);

    private const int Inset = 16, HoldMs = 1600, InMs = 140, OutMs = 260, CardW = 360;
    private static Window? _w;
    private static bool _hooked;
    private static (bool legend, Info info, bool animate, int hold)? _last;
    private static DispatcherTimer? _timer;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static SolidColorBrush B(Color c, double a = 1)
    {
        var b = new SolidColorBrush(a >= 1 ? c : Color.FromArgb((byte)(a * 255), c.R, c.G, c.B)); b.Freeze(); return b;
    }
    private static Color Col(TrayProfile p) { var t = Profiles.Tint(p); return Color.FromRgb(t.R, t.G, t.B); }
    private static string Spaced(string s) => string.Join("\u200A", s.ToUpperInvariant().ToCharArray());

    public static void Show(bool legend, Info info, bool animate, int holdMs = HoldMs)
    {
        var app = Application.Current;
        if (app is null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(new Action(() => Show(legend, info, animate, holdMs))); return; }

        Close();   // a new toast replaces the old one at once
        _last = (legend, info, animate, holdMs);
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
                if (_w == mine) Show(l.legend, l.info, false, 2000);
            }));
        }

        // theme colors (match the v2 mockup)
        var card = C(legend ? "#0F1318" : "#2A2C31");
        var border = C(legend ? "#1E252D" : "#34373D");
        var seg = C(legend ? "#11161B" : "#222428");
        var txt = C(legend ? "#E6EDF3" : "#FFFFFF");
        var subC = C(legend ? "#8A96A3" : "#A6ABB2");
        var dim = C(legend ? "#4A5561" : "#6E7A86");
        double r = legend ? 4 : 7;
        var tint = Col(info.Tint);
        var ui = app.TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI");
        var mono = app.TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Consolas");

        var g = new Grid { Width = CardW - 34 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // icon tile
        var fg = info.Lit ? tint : dim;
        var icon = new ContentControl
        {
            Content = app.TryFindResource(info.IconKey), Width = 22, Height = 22, Foreground = B(fg), Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        TextElement.SetForeground(icon, B(fg));
        g.Children.Add(new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(r), BorderThickness = new Thickness(1),
            Background = info.Lit ? B(tint, 0.14) : B(seg), BorderBrush = info.Lit ? B(tint, 0.7) : B(dim),
            VerticalAlignment = VerticalAlignment.Center, Child = icon,
        });

        var text = new StackPanel { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = legend ? Spaced(info.Title) : info.Title, FontFamily = legend ? mono : ui,
            FontSize = legend ? 13.5 : 15, FontWeight = legend ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = B(txt), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = info.Sub, FontFamily = ui, FontSize = 12, Foreground = B(subC), Margin = new Thickness(0, 5, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        FrameworkElement? right = null;
        switch (info.Right)
        {
            case Right.Pills:
            {
                var pills = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) };
                foreach (var q in new[] { TrayProfile.Quiet, TrayProfile.Balanced, TrayProfile.Performance })
                {
                    var qc = Col(q); bool on = q == info.Tint;
                    pills.Children.Add(new Border
                    {
                        Width = 14, Height = 7, Margin = new Thickness(6, 0, 0, 0), CornerRadius = new CornerRadius(legend ? 1 : 3.5),
                        Background = on ? B(qc) : legend ? Brushes.Transparent : B(dim, 0.5),
                        BorderBrush = legend ? B(qc, on ? 1 : 0.45) : null, BorderThickness = new Thickness(legend ? 1 : 0),
                    });
                }
                right = pills;
                break;
            }
            case Right.SwitchOn or Right.SwitchOff:
            {
                bool on = info.Right == Right.SwitchOn;
                var white = Colors.White;
                var track = new Grid { Width = 38, Height = 18, VerticalAlignment = VerticalAlignment.Top };
                if (legend)
                {
                    track.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
                        Background = on ? B(white, 0.14) : Brushes.Transparent, BorderBrush = on ? B(white) : B(dim),
                    });
                    track.Children.Add(new Border
                    {
                        Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Margin = new Thickness(3, 0, 3, 0),
                        Background = on ? B(white) : B(dim), VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    });
                }
                else
                {
                    track.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(on ? 0 : 1),
                        Background = on ? B(white) : Brushes.Transparent, BorderBrush = B(subC),
                    });
                    track.Children.Add(new System.Windows.Shapes.Ellipse
                    {
                        Width = on ? 9 : 7, Height = on ? 9 : 7, Margin = new Thickness(on ? 0 : 5.5, 0, on ? 4.5 : 0, 0),
                        Fill = on ? B(C("#0B0D10")) : B(subC), VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    });
                }
                right = track;
                break;
            }
            case Right.Chip:
                right = new Border
                {
                    CornerRadius = new CornerRadius(legend ? 3 : 10), BorderThickness = new Thickness(1),
                    Background = B(tint, 0.14), BorderBrush = B(tint, 0.6), Padding = new Thickness(9, 2, 9, 3),
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock
                    {
                        Text = legend ? Profiles.Label(info.Tint).ToUpperInvariant() : Profiles.Label(info.Tint),
                        FontFamily = legend ? mono : ui, FontSize = legend ? 10.5 : 11,
                        FontWeight = legend ? FontWeights.Bold : FontWeights.SemiBold, Foreground = B(tint),
                    },
                };
                break;
        }
        if (right is not null) { Grid.SetColumn(right, 2); g.Children.Add(right); }

        var cardB = new Border
        {
            Child = g, Width = CardW,
            Padding = new Thickness(16),
            Background = B(card), BorderBrush = B(border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(r + 2),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.5, Color = Colors.Black },
        };
        UIElement root = cardB;
        if (legend)
        {
            // Legend: 3 px preset-colored bar on the left edge
            var host = new Grid();
            host.Children.Add(cardB);
            host.Children.Add(new Border
            {
                Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = B(tint),
                Margin = new Thickness(0, 10, 0, 10),
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
            Opacity = animate && !WindowSlide.UseThread ? 0 : 1,
        };
        // 1.3.0-beta.1: with the new slide engine the toast window itself drops in from above the screen and slides
        // back up (refresh-timed thread) instead of fading a transparent window's opacity.
        bool slide = animate && WindowSlide.UseThread;
        var mover = new WindowSlide(() => new WindowInteropHelper(w).Handle);
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
            if (slide)
            {
                var h = new WindowInteropHelper(w).Handle;
                var r = WindowSlide.Rect(h);
                WindowSlide.Move(h, r.Left, r.Top - (r.Bottom - r.Top));
                mover.Run(r.Left, r.Top, InMs + 120, CubicBezier.Open, null);
            }
            else if (animate) w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(InMs)));
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
            if (slide)
            {
                var h = new WindowInteropHelper(w).Handle;
                var r = WindowSlide.Rect(h);
                mover.Run(r.Left, r.Top - (r.Bottom - r.Top), OutMs + 40, CubicBezier.Accelerate, () => { if (_w == w) Close(); });
                return;
            }
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
        try { w?.Close(); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("Toast.cs:256", __e); }
    }
}

/// <summary>v0.9.11: soft notification sounds (embedded WAVs, played async from memory, no extra packages).
/// v1.2: soft bell chimes (48 kHz mono, ~0.8 s, dev/chimes.py): one note per F2 preset (G5 / C6 / E6), G-Mode on
/// C6-E6-G6, off G6-C6, plug E5-B5, unplug B5-E5.</summary>
public static class Sounds
{
    public enum Kind { Quiet, Balanced, Performance, Unplug, Plug, GModeOn, GModeOff }

    [DllImport("winmm.dll")] private static extern bool PlaySound(IntPtr data, IntPtr mod, uint flags);
    private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;
    // unmanaged copies kept for the life of the app (async playback reads them after the call returns; ~600 KB if all are played)
    private static readonly System.Collections.Generic.Dictionary<Kind, IntPtr> _buf = new();

    public static void Play(Kind k)
    {
        try
        {
            if (!_buf.TryGetValue(k, out var p))
            {
                var name = k switch
                {
                    Kind.Quiet => "snd-quiet.wav", Kind.Balanced => "snd-balanced.wav", Kind.Performance => "snd-performance.wav",
                    Kind.Unplug => "snd-unplug.wav", Kind.Plug => "snd-plug.wav",
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
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("Toast.cs:295", __e); }
    }
}
