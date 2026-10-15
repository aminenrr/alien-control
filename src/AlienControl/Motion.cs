using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AlienControl;

/// <summary>
/// v0.8.2 micro-animations. Rules: only Opacity and RenderTransform (Scale / Translate) are animated,
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

    /// <summary>Runs the tracks together in ONE storyboard (60 fps cap, HoldEnd) and returns it.</summary>
    public static Storyboard Play(Action? completed, params (DependencyObject Target, DependencyProperty Property, AnimationTimeline Anim)[] tracks)
    {
        // TEMPORARY v0.8.5 diagnostics (MotionLog): which animation starts, finishes or throws.
        string label = string.Join("+", System.Linq.Enumerable.Select(tracks, t =>
            $"{(t.Target is FrameworkElement fe && fe.Name.Length > 0 ? fe.Name : t.Target.GetType().Name)}.{t.Property.Name}"));
        try
        {
            var sbx = PlayCore(completed, label, tracks);
            AlienControl.Services.MotionLog.Write($"begin {label}");
            return sbx;
        }
        catch (Exception ex)
        {
            AlienControl.Services.MotionLog.Write($"FAILED {label}: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private static Storyboard PlayCore(Action? completed, string label, (DependencyObject Target, DependencyProperty Property, AnimationTimeline Anim)[] tracks)
    {
        var sb = new Storyboard();
        Timeline.SetDesiredFrameRate(sb, Fps);
        foreach (var (target, prop, anim) in tracks)
        {
            Timeline.SetDesiredFrameRate(anim, Fps);
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, new PropertyPath(prop));
            sb.Children.Add(anim);
        }
        sb.Completed += (_, _) => AlienControl.Services.MotionLog.Write($"done {label}");
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

    private static FrameworkElement? Part(FrameworkElement el, string name) =>
        el is Control { Template: { } t } c ? t.FindName(name, c) as FrameworkElement : null;

    // ---------------------------------------------------------------- fx:Motion.Interactive (theme button styles)

    /// <summary>
    /// Set by the theme styles on buttons: hover = fade the template's "HoverFx" overlay (100 ms),
    /// press = scale 0.97 (70 ms) and back (120 ms), and for toggle buttons a selection "impact":
    /// the "Ul" / "Glow" indicator grows in (ScaleX 0 -> 1, 160 ms) and the button pulses 1 -> 1.03 -> 1 (180 ms).
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
        if (Part(el, "HoverFx") is not { } fx) { AlienControl.Services.MotionLog.Write($"no HoverFx in {el.GetType().Name} '{el.Name}' (Enabled={Enabled})"); return; }
        if (!(Enabled && el.IsVisible)) AlienControl.Services.MotionLog.Write($"hover skipped: Enabled={Enabled} IsVisible={el.IsVisible}");
        if (Enabled && el.IsVisible) Play(null, (fx, UIElement.OpacityProperty, Tween(null, on ? 1 : 0, 100, Quad)));
        else Set(fx, UIElement.OpacityProperty, on ? 1 : 0);
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        var el = (FrameworkElement)sender;
        if (!Enabled || !el.IsEnabled || !el.IsVisible) { AlienControl.Services.MotionLog.Write($"press skipped: Enabled={Enabled} IsEnabled={el.IsEnabled} IsVisible={el.IsVisible}"); return; }
        el.SetValue(PressedProperty, true);
        var s = Scale(el);
        Play(null, (s, ScaleTransform.ScaleXProperty, Tween(null, 0.97, 70, Quad)),
                   (s, ScaleTransform.ScaleYProperty, Tween(null, 0.97, 70, Quad)));
    }

    private static void OnUp(object sender, MouseButtonEventArgs e) => Release((FrameworkElement)sender);

    private static void Release(FrameworkElement el)
    {
        if (!(bool)el.GetValue(PressedProperty)) return;
        el.SetValue(PressedProperty, false);
        var s = Scale(el);
        if (!Enabled || !el.IsVisible) { Set(s, ScaleTransform.ScaleXProperty, 1); Set(s, ScaleTransform.ScaleYProperty, 1); return; }
        Play(() => Settle(el), (s, ScaleTransform.ScaleXProperty, Tween(null, 1, 120, Cubic)),
                   (s, ScaleTransform.ScaleYProperty, Tween(null, 1, 120, Cubic)));
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
            Play(null, (Scale(ind), ScaleTransform.ScaleXProperty, Tween(0, 1, 160, Cubic)));
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

    /// <summary>1 -> 1.03 -> 1 over 180 ms, starting from the current scale (e.g. 0.97 after a press).</summary>
    private static DoubleAnimationUsingKeyFrames Pulse()
    {
        var k = new DoubleAnimationUsingKeyFrames();
        k.KeyFrames.Add(new EasingDoubleKeyFrame(1.03, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)), Quad));
        k.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), Quad));
        return k;
    }
}
