using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AlienControl.Overlay;

/// <summary>
/// beta1.3-ov: the WPF host of the overlay HUD. ov.15: the layout and drawing moved to OverlayView (shared with the
/// Direct2D overlay window); this element is the Overlay tab preview and the WPF fallback window's content.
/// Set the View's values, then InvalidateMeasure (numbers changed) or InvalidateVisual (graph moved).
/// </summary>
public sealed class OverlayPanel : FrameworkElement
{
    public OverlayView View { get; } = new();

    private readonly WpfCanvas _canvas;

    public OverlayPanel() { _canvas = new WpfCanvas(this); }

    /// <summary>Kept for the tests and callers from before ov.15.</summary>
    internal static string ModeLabel(string m) => OverlayView.ModeLabel(m);

    protected override Size MeasureOverride(Size available)
    {
        _canvas.Dc = null;
        var (w, h) = View.Measure(_canvas);
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        _canvas.Dc = dc;
        View.Render(_canvas);
        _canvas.Dc = null;
    }

    /// <summary>IOverlayCanvas on a WPF DrawingContext. Frozen brushes and typefaces, FormattedText per string.</summary>
    private sealed class WpfCanvas : IOverlayCanvas
    {
        private static readonly Typeface Face = new(new FontFamily("Ubuntu, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Typeface FaceBold = new(new FontFamily("Ubuntu, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        private readonly Visual _owner;
        private readonly Dictionary<uint, Brush> _brushes = new();
        public DrawingContext? Dc;

        public WpfCanvas(Visual owner) { _owner = owner; }

        private double Dpi => VisualTreeHelper.GetDpi(_owner).PixelsPerDip;

        private Brush B(Rgba c)
        {
            uint key = (uint)c.A << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;
            if (_brushes.TryGetValue(key, out var b)) return b;
            var x = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            x.Freeze();
            if (_brushes.Count > 64) _brushes.Clear();
            _brushes[key] = x;
            return x;
        }

        private FormattedText T(string s, double size, bool bold, Brush? b = null) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, b ?? Brushes.White, Dpi);

        public double TextWidth(string s, double size, bool bold) => s.Length == 0 ? 0 : T(s, size, bold).WidthIncludingTrailingWhitespace;

        public (double Height, double Baseline) LineMetrics(string s, double size, bool bold)
        {
            var t = T(s.Length == 0 ? " " : s, size, bold);
            return (t.Height, t.Baseline);
        }

        public void Text(string s, double x, double y, double size, bool bold, Rgba c)
        {
            if (Dc is null || s.Length == 0) return;
            Dc.DrawText(T(s, size, bold, B(c)), new Point(x, y));
        }

        public void Fill(double x, double y, double w, double h, Rgba c)
        {
            if (Dc is null || w <= 0 || h <= 0) return;
            Dc.DrawRectangle(B(c), null, new Rect(x, y, w, h));
        }

        public void Polyline(ReadOnlySpan<double> pts, Rgba c, double width)
        {
            if (Dc is null || pts.Length < 4) return;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(pts[0], pts[1]), false, false);
                for (int i = 2; i + 1 < pts.Length; i += 2) ctx.LineTo(new Point(pts[i], pts[i + 1]), true, false);
            }
            g.Freeze();
            var pen = new Pen(B(c), width) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            Dc.DrawGeometry(null, pen, g);
        }
    }
}
