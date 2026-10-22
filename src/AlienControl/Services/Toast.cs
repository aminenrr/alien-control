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
/// 1.3.0-beta.2: two styles, <see cref="ToastStyle.Pill"/> (top-center capsule, default) and Classic (below).
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
    /// <param name="Oc">1.3.4: GPU OC tag added after Sub ("OC +200 / +200" or "OC off"); null = none.</param>
    /// <param name="OcLit">True draws the OC tag in the Performance tint (OC running), false in the sub text grey.</param>
    public sealed record Info(TrayProfile Tint, string IconKey, string Title, string Sub, Right Right = Right.None, bool Lit = true,
                              string? Oc = null, bool OcLit = false);

    private const int Inset = 16, HoldMs = 1600, InMs = 140, OutMs = 260, CardW = 360;
    private const int PillInMs = 220, PillOutMs = 160, PillW = 440, PillH = 62, PillOcH = 76;   // 1.3.1-beta.2: was 640 x 76 (dead space); 1.3.5: 76 with an OC line
    private static int PillHeight(Info info) => info.Oc is null ? PillH : PillOcH;

    public enum ToastStyle { Pill, Classic }
    /// <summary>1.3.0-beta.2: Pill (top-center capsule, default) or Classic (the v1.2 top-right card).</summary>
    public static ToastStyle Style { get; set; } = ToastStyle.Pill;
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
    /// <summary>1.3.4: the sub line, with the OC tag as its own run (Performance tint when on).</summary>
    private static TextBlock SubLine(Info info, Color subC, FontFamily ui, double size, double top, bool ocInline = true)
    {
        var tb = new TextBlock
        {
            FontFamily = ui, FontSize = size, Foreground = B(subC), Margin = new Thickness(0, top, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        tb.Inlines.Add(new System.Windows.Documents.Run(info.Sub));
        if (ocInline && info.Oc is { } oc)
        {
            tb.Inlines.Add(new System.Windows.Documents.Run(" · "));
            tb.Inlines.Add(new System.Windows.Documents.Run(oc)
            {
                Foreground = info.OcLit ? B(Col(TrayProfile.Performance)) : B(subC),
                FontWeight = info.OcLit ? FontWeights.SemiBold : FontWeights.Normal,
            });
        }
        return tb;
    }

    /// <summary>1.3.4-beta.4: Classic card is narrow, so the OC tag gets its own third line there.</summary>
    private static TextBlock? OcLine(Info info, Color subC, FontFamily ui, double size)
    {
        if (info.Oc is not { } oc) return null;
        return new TextBlock
        {
            Text = oc, FontFamily = ui, FontSize = size, Margin = new Thickness(0, 3, 0, 0),
            Foreground = info.OcLit ? B(Col(TrayProfile.Performance)) : B(subC),
            FontWeight = info.OcLit ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
    }

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

        bool pill = Style == ToastStyle.Pill;
        var root = pill ? BuildPill(app, legend, info) : BuildClassic(app, legend, info);
        // 1.3.0-beta.1: the toast window itself drops in from above the screen and slides back up (refresh-timed
        // thread) instead of fading a transparent window's opacity. beta.2: with Reduce motion / Windows animations
        // off the pill fades only (the classic card stays instant, as in 1.2).
        bool slide = animate;
        bool fade = !animate && pill;

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
            Opacity = fade ? 0 : 1,
        };
        var mover = new WindowSlide(() => new WindowInteropHelper(w).Handle);
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        w.Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;   // primary screen, minus a docked taskbar (top-docked included)
            w.Left = pill ? Math.Round(wa.Left + (wa.Width - w.Width) / 2) : wa.Right - w.Width - Inset + pad;
            w.Top = wa.Top + Inset - pad;
            if (slide)
            {
                var h = new WindowInteropHelper(w).Handle;
                var r = WindowSlide.Rect(h);
                WindowSlide.Move(h, r.Left, r.Top - (r.Bottom - r.Top));
                mover.Run(r.Left, r.Top, pill ? PillInMs : InMs + 120, CubicBezier.Open, null);
            }
            else if (fade) w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(InMs)));
        };
        // park off screen until Loaded positions it (no flash at 0,0)
        w.Left = -10000; w.Top = -10000;
        _w = w;
        w.Show();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(holdMs + (pill ? PillInMs : InMs)) };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            if ((!slide && !fade) || _w != w) { Close(); return; }
            if (slide)
            {
                var h = new WindowInteropHelper(w).Handle;
                var r = WindowSlide.Rect(h);
                mover.Run(r.Left, r.Top - (r.Bottom - r.Top), pill ? PillOutMs : OutMs + 40, CubicBezier.Accelerate, () => { if (_w == w) Close(); });
                return;
            }
            var fo = new DoubleAnimation(w.Opacity, 0, TimeSpan.FromMilliseconds(pill ? PillOutMs : OutMs));
            fo.Completed += (_, _) => { if (_w == w) Close(); };
            w.BeginAnimation(UIElement.OpacityProperty, fo);
        };
        _timer.Start();
    }


    /// <summary>v1.2 card (top-right): 44 px tile, title + detail, state on the right.</summary>
    private static UIElement BuildClassic(Application app, bool legend, Info info)
    {
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
            Width = 44, Height = 44, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1),
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
        text.Children.Add(SubLine(info, subC, ui, 12, 5, ocInline: false));
        if (OcLine(info, subC, ui, 12) is { } ocLine) text.Children.Add(ocLine);
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
            CornerRadius = new CornerRadius(22),   // 1.3.1-beta.2: rounded (was 6 Legend / 9 Fluent)
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
                Margin = new Thickness(1, 18, 0, 18), CornerRadius = new CornerRadius(1.5),   // inside the 22 px curve
            });
            root = host;
        }
        return root;
    }

    /// <summary>1.3.0-beta.2 pill (Amine's handoff mockups toasts-legend.png / toasts-fluent.png): 440 x 62 capsule since 1.3.1-beta.2 (was 640 x 76),
    /// round icon tile tinted in the preset color, title + detail, state on the right (G-Mode switch, F2 position dots,
    /// preset chip). Legend: #0F1318 with a preset-tinted border and a soft glow under the icon. Fluent: #2A2C31 with a
    /// neutral border and the Fluent accent switch.</summary>
    private static UIElement BuildPill(Application app, bool legend, Info info)
    {
        var card = C(legend ? "#0F1318" : "#2A2C31");
        var neutral = C(legend ? "#262E37" : "#3A3D44");
        var txt = C(legend ? "#E6EDF3" : "#FFFFFF");
        var subC = C(legend ? "#8A96A3" : "#A6ABB2");
        var dim = C(legend ? "#4A5561" : "#6E7A86");
        var tint = Col(info.Tint);
        bool lit = info.Lit;
        var ui = app.TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI");
        var accent = legend ? Colors.White : (app.TryFindResource("AccentBrush") as SolidColorBrush)?.Color ?? C("#4CC2FF");

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // round icon tile
        var fg = lit ? tint : dim;
        var icon = new ContentControl
        {
            Content = app.TryFindResource(info.IconKey), Width = 20, Height = 20, Foreground = B(fg), Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        TextElement.SetForeground(icon, B(fg));
        g.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
            Background = lit ? B(tint, legend ? 0.13 : 0.16) : B(neutral, 0.8),
            VerticalAlignment = VerticalAlignment.Center, Child = icon,
        });

        var text = new StackPanel { Margin = new Thickness(13, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = info.Title, FontFamily = ui, FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = B(lit ? txt : subC), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        // 1.3.5: the pill is 440 wide since 1.3.1, too narrow for the OC tag inline, so it gets its own line (pill grows)
        text.Children.Add(SubLine(info, subC, ui, 12.5, 2, ocInline: false));
        if (OcLine(info, subC, ui, 12.5) is { } ocLine) text.Children.Add(ocLine);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        FrameworkElement? right = null;
        switch (info.Right)
        {
            case Right.Pills:
            {
                // three position dots (Quiet / Balanced / Performance), the active one stretched and tinted
                var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                foreach (var q in new[] { TrayProfile.Quiet, TrayProfile.Balanced, TrayProfile.Performance })
                {
                    bool on = q == info.Tint;
                    dots.Children.Add(new Border
                    {
                        Width = on ? 15 : 6, Height = on ? 7 : 6, Margin = new Thickness(6, 0, 0, 0),
                        CornerRadius = new CornerRadius(4), Background = on ? B(Col(q)) : B(dim, 0.7),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                right = dots;
                break;
            }
            case Right.SwitchOn or Right.SwitchOff:
            {
                bool on = info.Right == Right.SwitchOn;
                var track = new Grid { Width = 44, Height = 22, VerticalAlignment = VerticalAlignment.Center };
                track.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(11),
                    Background = on ? B(accent) : B(neutral),
                });
                track.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 15, Height = 15, Margin = new Thickness(3.5, 0, 3.5, 0),
                    Fill = on ? B(C("#14181D")) : B(dim), VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                });
                right = track;
                break;
            }
            case Right.Chip:
                right = new Border
                {
                    CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Height = 24,
                    Background = B(tint, 0.12), BorderBrush = B(tint, 0.75), Padding = new Thickness(10, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = legend ? Profiles.Label(info.Tint).ToUpperInvariant() : Profiles.Label(info.Tint),
                        FontFamily = ui, FontSize = legend ? 10.5 : 12, VerticalAlignment = VerticalAlignment.Center,
                        FontWeight = legend ? FontWeights.Bold : FontWeights.SemiBold, Foreground = B(tint),
                    },
                };
                break;
        }
        if (right is not null) { right.Margin = new Thickness(0, 0, 6, 0); Grid.SetColumn(right, 2); g.Children.Add(right); }

        var capsule = new Border
        {
            Child = g, Width = PillW, Height = PillHeight(info), Padding = new Thickness(11, 0, 16, 0),
            Background = B(card), BorderThickness = new Thickness(1),
            BorderBrush = legend && lit ? B(tint, 0.55) : B(neutral),
            CornerRadius = new CornerRadius(PillHeight(info) / 2.0),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        if (!legend || !lit) return capsule;

        // Legend: soft preset-colored glow under the icon side, drawn behind the capsule
        var host = new Grid();
        var glow = new RadialGradientBrush { GradientOrigin = new Point(0.5, 0.5) };
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, tint.R, tint.G, tint.B), 0));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 1));
        glow.Freeze();
        host.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 160, Height = 28, Fill = glow, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(18, 0, 0, -14),
        });
        host.Children.Add(capsule);
        return host;
    }

    private static void Close()
    {
        _timer?.Stop(); _timer = null;
        var w = _w; _w = null;
        try { w?.Close(); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("Toast.cs:256", __e); }
    }
}

