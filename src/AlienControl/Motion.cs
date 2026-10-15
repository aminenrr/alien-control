using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AlienControl;

/// <summary>
/// v0.8.2 micro-animations (v0.8.6: Quick Settings style window slide, stronger values). Rules: only Opacity and
/// RenderTransform (Scale / Translate) are animated,
/// every storyboard is one-shot (no RepeatBehavior, no timers) with DesiredFrameRate = 60, short EaseOut curves.
/// Nothing runs while the window is hidden, so idle cost stays zero.
/// <see cref="Enabled"/> = false is "Reduce motion": every animation is skipped and values jump to their end
/// state, which is exactly the v0.8.1 behaviour.
/// </summary>
public static class Motion
{
    /// <summary>False when the user turned on Reduce motion (gear menu).</summary>
    public static bool Enabled { get; set; } = true;

    public const int Fps = 60;

    // v0.8.6 timings: the v0.8.2 values (70-180 ms, 0.97 / 1.03 scale, ~3 RGB-step hover) ran but were too faint to see.
    public const int HoverMs = 150, PressMs = 90, ReleaseMs = 160, SelectMs = 220;
    public const double PressScale = 0.95;
    public static readonly IEasingFunction Cubic = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction Quad = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseOut });

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // ---------------------------------------------------------------- building blocks

    /// <summary>A DoubleAnimation; from = null starts at the current (possibly animated) value.</summary>
    public static DoubleAnimation Tween(double? from, double to, int ms, IEasingFunction? ease = null)
    {
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease ?? Cubic };
        if (from is { } f) a.From = f;
        return a;
    }

    /// <summary>
    /// A Fluent-style curve as a one-keyframe spline animation (WPF easing functions can't take a cubic-bezier).
    /// from = null starts at the current (possibly animated) value.
    /// </summary>
    public static DoubleAnimationUsingKeyFrames Spline(double? from, double to, int ms, KeySpline curve)
    {
        var k = new DoubleAnimationUsingKeyFrames();
        if (from is { } f) k.KeyFrames.Add(new DiscreteDoubleKeyFrame(f, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        k.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), curve));
        return k;
    }

    /// <summary>Fluent "decelerate" (fast start, long soft stop): flyouts sliding in.</summary>
    public static KeySpline Decelerate => new(0, 0, 0, 1);
    /// <summary>Accelerate (slow start, quick exit): flyouts sliding out.</summary>
    public static KeySpline Accelerate => new(0.7, 0, 1, 0.5);

    /// <summary>Runs the tracks together in ONE storyboard (60 fps cap, HoldEnd) and returns it.</summary>
    public static Storyboard Play(Action? completed, params (DependencyObject Target, DependencyProperty Property, AnimationTimeline Anim)[] tracks)
    {
        var sb = new Storyboard();
        Timeline.SetDesiredFrameRate(sb, Fps);
        foreach (var (target, prop, anim) in tracks)
        {
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, new PropertyPath(prop));
            sb.Children.Add(anim);
        }
        if (completed is not null) sb.Completed += (_, _) => completed();
        sb.Begin();   // SnapshotAndReplace: a new run takes over smoothly from the current value
        return sb;
    }

    /// <summary>Drops any animation on the property and sets the value instantly.</summary>
    public static void Set(DependencyObject target, DependencyProperty property, double value)
    {
        if (target is IAnimatable a) a.BeginAnimation(property, null);
        target.SetValue(property, value);
    }

    /// <summary>The element's own (unfrozen) ScaleTransform, created on first use.</summary>
    public static ScaleTransform Scale(UIElement el)
    {
        if (el.RenderTransform is ScaleTransform { IsFrozen: false } s) return s;
        s = new ScaleTransform(1, 1);
        el.RenderTransform = s;
        return s;
    }

    /// <summary>The element's own (unfrozen) TranslateTransform, created on first use.</summary>
    public static TranslateTransform Translate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform { IsFrozen: false } t) return t;
        t = new TranslateTransform();
        el.RenderTransform = t;
        return t;
    }

    /// <summary>A named part of a control template, or for a Panel (the RAM card) its child whose name ends in the name.</summary>
    private static FrameworkElement? Part(FrameworkElement el, string name) => el switch
    {
        Control { Template: { } t } c => t.FindName(name, c) as FrameworkElement,
        Panel p => System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<FrameworkElement>(p.Children), x => x.Name.EndsWith(name)),
        _ => null
    };

    // ---------------------------------------------------------------- fx:Motion.Interactive (theme button styles)

    /// <summary>
    /// Set by the theme styles on buttons: hover = fade the template's "HoverFx" overlay (150 ms),
    /// press = scale 0.95 (90 ms) and back (160 ms), and for toggle buttons a selection "impact":
    /// the "Ul" / "Glow" indicator grows in (ScaleX 0 -> 1, 220 ms) and the button pulses 1 -> 1.04 -> 1 (220 ms).
    /// </summary>
    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.RegisterAttached(
        "Interactive", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnInteractiveChanged));

    public static bool GetInteractive(DependencyObject d) => (bool)d.GetValue(InteractiveProperty);
    public static void SetInteractive(DependencyObject d, bool value) => d.SetValue(InteractiveProperty, value);

    private static readonly DependencyProperty PressedProperty = DependencyProperty.RegisterAttached(
        "Pressed", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    private static void OnInteractiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.MouseEnter -= OnEnter; el.MouseLeave -= OnLeave;
        el.PreviewMouseLeftButtonDown -= OnDown; el.PreviewMouseLeftButtonUp -= OnUp;
        if (el is ToggleButton tb0) tb0.Checked -= OnChecked;
        if (!(bool)e.NewValue) return;
        el.MouseEnter += OnEnter; el.MouseLeave += OnLeave;
        el.PreviewMouseLeftButtonDown += OnDown; el.PreviewMouseLeftButtonUp += OnUp;
        if (el is ToggleButton tb) tb.Checked += OnChecked;
    }

    private static void OnEnter(object sender, MouseEventArgs e) => Hover((FrameworkElement)sender, true);

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        var el = (FrameworkElement)sender;
        Hover(el, false);
        Release(el);
    }

    private static void Hover(FrameworkElement el, bool on)
    {
        if (Part(el, "HoverFx") is not { } fx) return;
        if (Enabled && el.IsVisible) Play(null, (fx, UIElement.OpacityProperty, Tween(null, on ? 1 : 0, HoverMs, Quad)));
        else Set(fx, UIElement.OpacityProperty, on ? 1 : 0);
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        var el = (FrameworkElement)sender;
        if (!Enabled || !el.IsEnabled || !el.IsVisible) return;
        el.SetValue(PressedProperty, true);
        var s = Scale(el);
        Play(null, (s, ScaleTransform.ScaleXProperty, Tween(null, PressScale, PressMs, Quad)),
                   (s, ScaleTransform.ScaleYProperty, Tween(null, PressScale, PressMs, Quad)));
    }

    private static void OnUp(object sender, MouseButtonEventArgs e) => Release((FrameworkElement)sender);

    private static void Release(FrameworkElement el)
    {
        if (!(bool)el.GetValue(PressedProperty)) return;
        el.SetValue(PressedProperty, false);
        var s = Scale(el);
        if (!Enabled || !el.IsVisible) { Set(s, ScaleTransform.ScaleXProperty, 1); Set(s, ScaleTransform.ScaleYProperty, 1); return; }
        Play(() => Settle(el), (s, ScaleTransform.ScaleXProperty, Tween(null, 1, ReleaseMs, Cubic)),
                   (s, ScaleTransform.ScaleYProperty, Tween(null, 1, ReleaseMs, Cubic)));
    }

    /// <summary>Selection impact: runs for clicks and for state read back from hardware while the window is open.</summary>
    private static void OnChecked(object sender, RoutedEventArgs e)
    {
        var el = (FrameworkElement)sender;
        if (!Enabled || !el.IsVisible) return;
        el.SetValue(PressedProperty, false);   // the pulse takes over from the press
        foreach (var name in new[] { "Ul", "Glow" })
        {
            if (Part(el, name) is not { } ind) continue;
            ind.RenderTransformOrigin = new Point(0.5, 0.5);
            Play(null, (Scale(ind), ScaleTransform.ScaleXProperty, Tween(0, 1, SelectMs, Cubic)));
        }
        var s = Scale(el);
        Play(() => Settle(el), (s, ScaleTransform.ScaleXProperty, Pulse()), (s, ScaleTransform.ScaleYProperty, Pulse()));
    }

    /// <summary>After a press/pulse ends (and no new press started), remove the transform entirely so the
    /// button's text is laid out on whole device pixels again (crisp, ClearType) instead of under a held scale.</summary>
    private static void Settle(FrameworkElement el)
    {
        if ((bool)el.GetValue(PressedProperty)) return;
        if (el.RenderTransform is ScaleTransform s && !s.IsFrozen)
        {
            s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            el.RenderTransform = Transform.Identity;
        }
    }

    /// <summary>1 -> 1.04 -> 1 over 220 ms, starting from the current scale (e.g. 0.97 after a press).</summary>
    private static DoubleAnimationUsingKeyFrames Pulse()
    {
        var k = new DoubleAnimationUsingKeyFrames();
        k.KeyFrames.Add(new EasingDoubleKeyFrame(1.04, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)), Quad));
        k.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), Quad));
        return k;
    }
}
