using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AlienControl.Overlay;

/// <summary>
/// beta1.3-ov: the Steam style overlay slider (OFF 1 2 3 4) from the mockups, on Home and on the Overlay tab.
/// Click or drag to a stop; arrow keys move one stop. Raises LevelPicked only for user input.
/// </summary>
public sealed class LevelSlider : Grid
{
    private readonly Ellipse[] _knobs = new Ellipse[5];
    private readonly Rectangle[] _ticks = new Rectangle[5];
    private readonly TextBlock[] _labels = new TextBlock[5];
    private readonly Border _fill;
    private readonly UniformGrid _stops;
    private int _level;
    private bool _drag;

    public event Action<int>? LevelPicked;

    public LevelSlider()
    {
        Height = 50;
        Focusable = true;
        Background = Brushes.Transparent;   // hit testing over the whole strip
        Cursor = Cursors.Hand;
        AutomationProperties.SetName(this, "Overlay level");
        FocusVisualStyle = null;

        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 0, 0) };
        track.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x21, 0x27));
        Children.Add(track);
        _fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        _fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        Children.Add(_fill);

        _stops = new UniformGrid { Columns = 5, Margin = new Thickness(-47, 0, -47, 0), IsHitTestVisible = false };
        string[] names = { "OFF", "1", "2", "3", "4" };
        for (int i = 0; i < 5; i++)
        {
            var g = new Grid();
            _knobs[i] = new Ellipse { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0), Fill = new SolidColorBrush(Color.FromRgb(0xE9, 0xEE, 0xF2)) };
            _ticks[i] = new Rectangle { Width = 2, Height = 7, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 24, 0, 0) };
            _labels[i] = new TextBlock { Text = names[i], HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 34, 0, 0) };
            _labels[i].SetResourceReference(StyleProperty, "SecHint2");
            g.Children.Add(_ticks[i]);
            g.Children.Add(_knobs[i]);
            g.Children.Add(_labels[i]);
            _stops.Children.Add(g);
        }
        Children.Add(_stops);
        SizeChanged += (_, _) => Paint();
        Paint();
    }

    public int Level
    {
        get => _level;
        set { value = Math.Clamp(value, 0, OverlayLevels.Max); if (value == _level) return; _level = value; Paint(); }
    }

    private void Paint()
    {
        for (int i = 0; i < 5; i++)
        {
            bool on = i == _level;
            _knobs[i].Visibility = on ? Visibility.Visible : Visibility.Hidden;
            if (on) _ticks[i].SetResourceReference(Shape.FillProperty, "AccentBrush");
            else _ticks[i].Fill = new SolidColorBrush(i < _level ? Color.FromRgb(0x5A, 0x63, 0x6C) : Color.FromRgb(0x2A, 0x31, 0x38));
            _labels[i].Foreground = new SolidColorBrush(on ? Color.FromRgb(0xF2, 0xF6, 0xF9) : Color.FromRgb(0x8A, 0x95, 0xA0));
            _labels[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (ActualWidth > 0) _stops.Margin = new Thickness(-ActualWidth / 8, 0, -ActualWidth / 8, 0);   // stop i sits at width * i / 4
        _fill.Width = ActualWidth <= 0 ? 0 : ActualWidth * _level / 4.0;
        AutomationProperties.SetItemStatus(this, _level == 0 ? "Off" : $"Level {_level}");
    }

    private int StopAt(double x) => (int)Math.Clamp(Math.Round(x / Math.Max(1, ActualWidth) * 4), 0, 4);

    private void Pick(int level)
    {
        if (level == _level) return;
        Level = level;
        LevelPicked?.Invoke(level);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _drag = true;
        CaptureMouse();
        Focus();
        Pick(StopAt(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag) Pick(StopAt(e.GetPosition(this).X));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _drag = false;
        ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Left or Key.Down) { Pick(Math.Max(0, _level - 1)); e.Handled = true; }
        else if (e.Key is Key.Right or Key.Up) { Pick(Math.Min(4, _level + 1)); e.Handled = true; }
    }
}
