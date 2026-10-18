using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace AlienControl.Services;

/// <summary>
/// v0.8.7: the tray right-click menu drawn in the app's look. WinForms can't read the WPF theme XAML, so the two
/// palettes live here: Legend (near-black, mono, caps, square) and Fluent (Windows 11 dark gray, Segoe UI, rounded).
/// Text is the accent color in both, so it follows the lighting like the app. Nothing runs while the menu is closed.
/// </summary>
public sealed class TrayMenuStyle
{
    public bool Legend { get; private set; } = true;
    public Color Accent { get; private set; } = Color.FromArgb(0, 229, 255);

    public Color Back => Legend ? Color.FromArgb(0x0E, 0x10, 0x13) : Color.FromArgb(0x2C, 0x2C, 0x2C);
    public Color Border => Legend ? Color.FromArgb(0x23, 0x29, 0x30) : Color.FromArgb(0x40, 0x40, 0x40);
    public Color Separator => Legend ? Color.FromArgb(0x1C, 0x21, 0x27) : Color.FromArgb(0x3D, 0x3D, 0x3D);
    /// <summary>Hover: a faint accent tint over the background (Legend) or a soft lighter gray (Fluent).</summary>
    public Color Hover => Legend ? Blend(Back, Accent, 0.14) : Blend(Color.FromArgb(0x38, 0x38, 0x38), Accent, 0.06);

    public void Set(bool legend, Color accent) { Legend = legend; Accent = accent; }

    private static Color Blend(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>First installed font of the list (WinForms silently substitutes a missing one).</summary>
    public Font MakeFont()
    {
        // 1.3.1 (Amine): the app's text font (UiFont) in both themes; Legend used Cascadia Mono caps before
        var (names, size) = (new[] { "Segoe UI Variable Text", "Segoe UI" }, 9.5f);
        foreach (var n in names)
        {
            var f = new Font(n, size, FontStyle.Regular, GraphicsUnit.Point);
            if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose();
        }
        return new Font(WinForms.Control.DefaultFont.FontFamily, size);
    }

    // ---------- Windows 11 corners (no-op on Windows 10) ----------
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;

    public void ApplyCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int pref = DWMWCP_ROUND;   // 1.3.1: rounded in both themes, like the app window (Legend was square)
        try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("TrayMenu.cs:53", __e); }
    }
}

public sealed class TrayMenuRenderer : WinForms.ToolStripRenderer
{
    private readonly TrayMenuStyle _s;
    public TrayMenuRenderer(TrayMenuStyle style) { _s = style; }

    protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(_s.Back);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderImageMargin(WinForms.ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(_s.Back);   // no light gutter on the left
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
    {
        // 1.3.1: rounded in both themes, so the DWM frame draws the edge (a square border would show in the corners)
    }

    protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
    {
        var r = e.Item.ContentRectangle;
        int y = r.Top + r.Height / 2;
        using var p = new Pen(_s.Separator);
        e.Graphics.DrawLine(p, r.Left + 8, y, r.Right - 8, y);
    }

    protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        var r = new Rectangle(Point.Empty, e.Item.Size);
        using var b = new SolidBrush(_s.Hover);
        // 1.3.1 (Amine): a rounded highlight like the app's hover, in both themes (Legend had a square row + accent bar)
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(new Rectangle(r.X + 4, r.Y + 1, r.Width - 9, r.Height - 3), 5);
        g.FillPath(b, path);
        g.SmoothingMode = SmoothingMode.Default;
    }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        // 1.3.1: a preset's name is in its own color (item.Tag); greyed when disabled (Battery on AC); the rest accent
        e.TextColor = !e.Item.Enabled ? Color.FromArgb(0x6E, 0x7A, 0x86) : e.Item.Tag is Color c ? c : _s.Accent;
        base.OnRenderItemText(e);
    }

    /// <summary>The current profile: an accent check drawn by hand (the default one is a light-theme bitmap).</summary>
    protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        var r = e.ImageRectangle;
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = new Pen(_s.Accent, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(p, new[] { new PointF(cx - 4.5f, cy + 0.5f), new PointF(cx - 1.5f, cy + 3.5f), new PointF(cx + 4.5f, cy - 3.5f) });
        g.SmoothingMode = SmoothingMode.Default;
    }

    private static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>A small filled dot in the profile's tray tint, shown in the image column.</summary>
    public static Bitmap Dot(Color c, int size = 16)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(c);
        float d = size * 0.45f, o = (size - d) / 2f;
        g.FillEllipse(b, o, o, d, d);
        return bmp;
    }
}
