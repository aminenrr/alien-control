using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AlienControl.Overlay;

/// <summary>
/// beta1.3-ov: draws the MangoHud HUD in one OnRender pass (no element tree, frozen brushes, one geometry for the
/// graph). Used by the in-game overlay window and by the preview on the Overlay tab.
/// Look copied from MangoHud defaults: background #020202 at 50 %, square corners, white text, labels in the
/// MangoHud colours, units in a smaller font, frametime graph in green.
/// </summary>
public sealed class OverlayPanel : FrameworkElement
{
    // MangoHud default colours (src/overlay_params.h)
    private static readonly Brush White = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush GpuC = Frozen(0x2E, 0x97, 0x62);
    private static readonly Brush CpuC = Frozen(0x2E, 0x97, 0xCB);
    private static readonly Brush VramC = Frozen(0xAD, 0x64, 0xC1);
    private static readonly Brush RamC = Frozen(0xC2, 0x66, 0x93);
    private static readonly Brush EngineC = Frozen(0xEB, 0x5B, 0x5B);
    private static readonly Brush BattC = Frozen(0xFF, 0x90, 0x78);
    private static readonly Brush FanC = Frozen(0xE0, 0x7B, 0x85);
    private static readonly Brush CoreE = Frozen(0x7F, 0xB8, 0xDB);   // E cores: a lighter CPU blue
    private static readonly Brush DimC = Frozen(0xA0, 0xA0, 0xA0);
    private static readonly Pen GraphPen = FrozenPen(Color.FromRgb(0x00, 0xFF, 0x00), 1.4);

    private static readonly Typeface Face = new(new FontFamily("Ubuntu, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface FaceLabel = new(new FontFamily("Ubuntu, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    private static Pen FrozenPen(Color c, double w) { var b = new SolidColorBrush(c); b.Freeze(); var p = new Pen(b, w) { LineJoin = PenLineJoin.Round }; p.Freeze(); return p; }

    // ---------- inputs (set from the owning thread, then InvalidateMeasure) ----------
    public int Level { get; set; } = 3;
    public Item Items { get; set; } = OverlayLevels.L3;
    public double Scale { get; set; } = 0.8;
    public double BgAlpha { get; set; } = 0.5;
    public Sensors Data { get; set; } = new();
    public double Fps { get; set; }
    public double FrameMs { get; set; }
    /// <summary>Generated fps (frame generation), 0 when none seen. Shown after the real fps, Steam style, behind a line.</summary>
    public double GenFps { get; set; }
    /// <summary>Latency in ms, NaN when unknown. LatPc: Reflex style PC latency instead of display latency.</summary>
    public double LatMs { get; set; } = double.NaN;
    public bool LatPc { get; set; }

    private bool ShowGen => (Items & Item.FrameGen) != 0 && GenFps >= 1;
    private string LatLabel => LatPc ? "PC LAT" : "LATENCY";
    private string LatValue => double.IsNaN(LatMs) ? "--" : LatMs.ToString(LatMs < 10 ? "0.0" : "0");
    public string Engine { get; set; } = "";
    /// <summary>Recent frametimes, oldest first (the graph).</summary>
    public float[] Graph { get; set; } = Array.Empty<float>();
    public int GraphCount { get; set; }
    /// <summary>1 % low fps and max frametime over the graph window (level 4).</summary>
    public double Low1 { get; set; }
    public double Low01 { get; set; }
    /// <summary>ov.6: PresentMon's PresentMode string, empty when unknown.</summary>
    public string PresentMode { get; set; } = "";

    /// <summary>Short label for PresentMon's PresentMode. Independent flip is what G-Sync / VRR wants; composed means
    /// DWM is compositing the game (about a frame more latency, VRR may drop).</summary>
    internal static string ModeLabel(string m) => m.Trim() switch
    {
        "Hardware: Independent Flip" => "IFLIP",
        "Hardware Composed: Independent Flip" => "IFLIP MPO",
        "Hardware: Legacy Flip" => "FULLSCREEN",
        "Hardware: Legacy Copy to front buffer" => "FS COPY",
        "Composed: Flip" => "COMPOSED",
        "Composed: Copy with GPU GDI" or "Composed: Copy with CPU GDI" => "COMPOSED COPY",
        _ => "--",
    };
    public double MaxMs { get; set; }

    private double Fs => 24 * Scale;            // MangoHud font_size 24
    private double FsUnit => Fs * 0.55;         // units are drawn smaller
    private double Pad => 6 * Scale;
    private double RowH => Fs * 1.18;

    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private FormattedText T(string s, Brush b, double size, Typeface? f = null) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, f ?? Face, size, b, Dpi);

    // ---------- layout model ----------

    /// <summary>One value cell: the number and its unit.</summary>
    private readonly record struct Cell(string Value, string Unit);
    private readonly record struct Row(string Label, Brush Color, Cell[] Cells);

    private List<Row> BuildRows()
    {
        var d = Data;
        var it = Items;
        var rows = new List<Row>();
        if ((it & (Item.Gpu | Item.GpuTemp | Item.GpuPower | Item.GpuClocks)) != 0)
        {
            var c = new List<Cell>();
            if (d.GpuOff) c.Add(new("OFF", ""));
            else
            {
                if ((it & Item.Gpu) != 0) c.Add(new(N(d.GpuLoad), "%"));
                if ((it & Item.GpuTemp) != 0) c.Add(new(N(d.GpuTemp), "°C"));
                if ((it & Item.GpuPower) != 0) c.Add(new(N(d.GpuW), "W"));
                if ((it & Item.GpuClocks) != 0) { c.Add(new(N(d.GpuCoreMhz), "MHz")); c.Add(new(N(d.GpuMemMhz), "MHz")); }
            }
            rows.Add(new("GPU", GpuC, c.ToArray()));
        }
        if ((it & (Item.Cpu | Item.CpuTemp | Item.CpuPower | Item.CpuMhz)) != 0)
        {
            var c = new List<Cell>();
            if ((it & Item.Cpu) != 0) c.Add(new(N(d.CpuLoad), "%"));
            if ((it & Item.CpuTemp) != 0) c.Add(new(N(d.CpuTemp), "°C"));
            if ((it & Item.CpuPower) != 0) c.Add(new(N(d.CpuW), "W"));
            if ((it & Item.CpuMhz) != 0) c.Add(new(N(d.CpuMhz), "MHz"));
            rows.Add(new("CPU", CpuC, c.ToArray()));
        }
        if ((it & Item.CoreLoads) != 0 && d.Cores is { Length: > 0 } cores)
        {
            // one row per physical core: P1..P6 (two threads each), E1..E8 (one thread) on the i7-12700H,
            // each thread's load then the core's real clock; single thread cores pad so the clocks line up
            int maxThreads = 1;
            foreach (var c in cores) maxThreads = Math.Max(maxThreads, c.Loads.Length);
            foreach (var core in cores)
            {
                var c = new List<Cell>();
                for (int t = 0; t < maxThreads; t++) c.Add(t < core.Loads.Length ? new(core.Loads[t].ToString("0"), "%") : new("", ""));
                c.Add(new(N(core.Mhz), "MHz"));
                rows.Add(new($"{core.Kind}{core.Index}", core.Kind == 'E' ? CoreE : CpuC, c.ToArray()));
            }
        }
        if ((it & Item.Vram) != 0) rows.Add(new("VRAM", VramC, new[] { new Cell(G(d.VramGiB), "GiB") }));
        if ((it & Item.Ram) != 0) rows.Add(new("RAM", RamC, new[] { new Cell(G(d.RamGiB), "GiB") }));
        if ((it & Item.Battery) != 0)
        {
            var c = new List<Cell> { new(N(d.BatteryPct), "%") };
            if (d.OnAc) c.Add(new("AC", ""));
            else if (d.BatteryW is int w) c.Add(new(w.ToString(), "W"));
            rows.Add(new("BATT", BattC, c.ToArray()));
        }
        if ((it & Item.Fans) != 0)
            rows.Add(new("FAN", FanC, new[] { new Cell(N(d.CpuFanRpm), "RPM"), new Cell(N(d.GpuFanRpm), "RPM") }));
        if ((it & Item.Refresh) != 0) rows.Add(new("DISPLAY", DimC, new[] { new Cell(N(d.RefreshHz), "Hz") }));
        if ((it & Item.Engine) != 0)
        {
            var c = new List<Cell> { new(Fps.ToString("0"), "FPS") };
            if (ShowGen) c.Add(new("| " + GenFps.ToString("0"), "FG"));
            if ((it & Item.FrameTime) != 0) c.Add(new(FrameMs.ToString("0.0"), "ms"));
            rows.Add(new(string.IsNullOrEmpty(Engine) ? "FPS" : Engine, EngineC, c.ToArray()));
        }
        if ((it & Item.Latency) != 0) rows.Add(new(LatLabel, DimC, new[] { new Cell(LatValue, "ms") }));
        if ((it & Item.PresentMode) != 0) rows.Add(new("MODE", DimC, new[] { new Cell(ModeLabel(PresentMode), "") }));
        // ov.5: lows as normal rows, so they line up with the columns above
        if ((it & Item.Low1) != 0)
        {
            var c = new List<Cell> { new(Low1.ToString("0"), "FPS") };
            if ((it & Item.FrameStats) != 0) c.Add(new(MaxMs.ToString("0.0"), "ms max"));
            rows.Add(new("1% LOW", EngineC, c.ToArray()));
        }
        if ((it & Item.Low01) != 0) rows.Add(new("0.1% LOW", EngineC, new[] { new Cell(Low01.ToString("0"), "FPS") }));
        return rows;
    }

    private static string N(double? v) => v is double x ? x.ToString("0") : "--";
    private static string N(int? v) => v is int x ? x.ToString() : "--";
    private static string G(double? v) => v is double x ? x.ToString("0.0") : "--";

    // ---------- measure / render ----------

    private Size _size;

    protected override Size MeasureOverride(Size available)
    {
        _size = Level switch
        {
            1 => MeasureL1(),
            2 => MeasureHorizontal(),
            >= 3 => MeasureVertical(),
            _ => new Size(0, 0),
        };
        return _size;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Level <= 0) return;
        var bg = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(BgAlpha, 0, 1) * 255), 2, 2, 2));
        bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(_size));
        if (Level == 1) RenderL1(dc);
        else if (Level == 2) RenderHorizontal(dc);
        else RenderVertical(dc);
    }

    // level 1: the fps number alone (MangoHud fps_only)
    // ov.5: the fps, then the 1 % low with a small "1%" unit (Amine)
    private bool L1Low => (Items & Item.Low1) != 0;
    private Cell L1LowCell => new(Low1.ToString("0"), "1%");
    private double L1FpsW => Math.Max(T(Fps.ToString("0"), White, Fs).WidthIncludingTrailingWhitespace, T("000", White, Fs).Width);

    private Size MeasureL1()
    {
        double w = L1FpsW + Pad * 2;
        if (L1Low) w += 10 * Scale + Math.Max(CellW(L1LowCell), CellW(new("000", "1%")));
        return new Size(Math.Ceiling(w), RowH + Pad * 2);
    }

    private void RenderL1(DrawingContext dc)
    {
        double right = Pad + L1FpsW;
        DrawCellRight(dc, new Cell(Fps.ToString("0"), ""), right, Pad);
        if (L1Low) DrawCellRight(dc, L1LowCell, _size.Width - Pad, Pad);
    }

    // ---------- vertical (levels 3 and 4) ----------

    private const int ValueCols = 5;
    private double[] _colW = new double[ValueCols];
    private double _labelW;
    private List<Row> _rows = new();
    private double GraphW => 260 * Scale;
    private double GraphH => 46 * Scale;

    private double CellW(Cell c) => T(c.Value, White, Fs).WidthIncludingTrailingWhitespace + (c.Unit.Length > 0 ? T(c.Unit, White, FsUnit).WidthIncludingTrailingWhitespace + 2 * Scale : 0);

    private Size MeasureVertical()
    {
        _rows = BuildRows();
        _labelW = 0;
        Array.Clear(_colW);
        foreach (var r in _rows)
        {
            _labelW = Math.Max(_labelW, T(r.Label, r.Color, Fs, FaceLabel).WidthIncludingTrailingWhitespace);
            for (int i = 0; i < r.Cells.Length && i < ValueCols; i++)
                _colW[i] = Math.Max(_colW[i], Math.Max(CellW(r.Cells[i]), T("100", White, Fs).Width + T("MHz", White, FsUnit).Width));
        }
        double w = Pad * 2 + _labelW;
        foreach (var c in _colW) if (c > 0) w += c + 10 * Scale;
        double h = Pad * 2 + _rows.Count * RowH;
        if ((Items & Item.FrameGraph) != 0)
        {
            w = Math.Max(w, GraphW + Pad * 2);
            h += GraphH + FsUnit * 1.4 + Pad;
        }
        return new Size(Math.Ceiling(w), Math.Ceiling(h));
    }

    private void RenderVertical(DrawingContext dc)
    {
        double y = Pad;
        foreach (var r in _rows)
        {
            var lab = T(r.Label, r.Color, Fs, FaceLabel);
            dc.DrawText(lab, new Point(Pad, y + (RowH - lab.Height) / 2));
            double x = Pad + _labelW;
            for (int i = 0; i < ValueCols; i++)
            {
                if (_colW[i] <= 0) break;
                x += _colW[i] + 10 * Scale;
                if (i < r.Cells.Length) DrawCellRight(dc, r.Cells[i], x, y);
            }
            y += RowH;
        }
        if ((Items & Item.FrameGraph) != 0)
        {
            y += Pad / 2;
            var cap = T("Frametime", White, FsUnit);
            dc.DrawText(cap, new Point(Pad, y));
            var range = T($"min {GraphMin():0.0} / max {GraphMax():0.0} ms", DimC, FsUnit);
            dc.DrawText(range, new Point(_size.Width - Pad - range.Width, y));
            y += FsUnit * 1.4;
            DrawGraph(dc, new Rect(Pad, y, _size.Width - Pad * 2, GraphH));
        }
    }

    private void DrawCellRight(DrawingContext dc, Cell c, double right, double y)
    {
        double x = right;
        if (c.Value.Length == 0) return;
        if (c.Unit.Length > 0)
        {
            var u = T(c.Unit, White, FsUnit);
            x -= u.WidthIncludingTrailingWhitespace;
            var v0 = T(c.Value, White, Fs);
            double baseY = y + (RowH - v0.Height) / 2 + v0.Baseline;
            dc.DrawText(u, new Point(x, baseY - u.Baseline));
            x -= 2 * Scale;
        }
        var v = T(c.Value, White, Fs);
        x -= v.WidthIncludingTrailingWhitespace;
        dc.DrawText(v, new Point(x, y + (RowH - v.Height) / 2));
    }

    // ---------- horizontal (level 2) ----------

    private readonly record struct HItem(string Label, Brush Color, Cell[] Cells, bool Graph);
    private List<HItem> _hitems = new();

    private List<HItem> BuildHorizontal()
    {
        var d = Data; var it = Items;
        var l = new List<HItem>();
        if ((it & Item.Fps) != 0)
        {
            // Steam style: real fps, then a line and the generated fps; only the real fps when no generated frames are seen
            var c = new List<Cell> { new(Fps.ToString("0"), "") };
            if (ShowGen) c.Add(new("| " + GenFps.ToString("0"), "FG"));
            l.Add(new("FPS", EngineC, c.ToArray(), false));
        }
        if ((it & Item.FrameTime) != 0) l.Add(new("", White, new[] { new Cell(FrameMs.ToString("0.0"), "ms") }, false));
        if ((it & Item.Latency) != 0) l.Add(new(LatPc ? "PC LAT" : "LAT", DimC, new[] { new Cell(LatValue, "ms") }, false));
        if ((it & Item.FrameGraph) != 0) l.Add(new("", White, Array.Empty<Cell>(), true));
        if ((it & Item.Cpu) != 0)
        {
            var c = new List<Cell> { new(N(d.CpuLoad), "%") };
            if ((it & Item.CpuPower) != 0) c.Add(new(N(d.CpuW), "W"));
            l.Add(new("CPU", CpuC, c.ToArray(), false));
        }
        if ((it & Item.Gpu) != 0)
        {
            var c = new List<Cell>();
            if (d.GpuOff) c.Add(new("OFF", ""));
            else { c.Add(new(N(d.GpuLoad), "%")); if ((it & Item.GpuPower) != 0) c.Add(new(N(d.GpuW), "W")); }
            l.Add(new("GPU", GpuC, c.ToArray(), false));
        }
        if ((it & Item.Ram) != 0) l.Add(new("RAM", RamC, new[] { new Cell(G(d.RamGiB), "GiB") }, false));
        if ((it & Item.Vram) != 0) l.Add(new("VRAM", VramC, new[] { new Cell(G(d.VramGiB), "GiB") }, false));
        if ((it & Item.Battery) != 0)
        {
            var c = new List<Cell> { new(N(d.BatteryPct), "%") };
            if (d.OnAc) c.Add(new("AC", "")); else if (d.BatteryW is int w) c.Add(new(w.ToString(), "W"));
            l.Add(new("BATT", BattC, c.ToArray(), false));
        }
        if ((it & Item.Refresh) != 0) l.Add(new("RR", DimC, new[] { new Cell(N(d.RefreshHz), "Hz") }, false));   // ov.5
        return l;
    }

    private double HGraphW => 110 * Scale;
    // ov.9 (Amine picked mockup B): a short, brighter bar between every item of the level 2 strip
    private double Gap => 20 * Scale;
    private static readonly Brush DividerBrush = FrozenBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));   // white at 45 %

    private static Brush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private double HItemW(HItem h)
    {
        if (h.Graph) return HGraphW;
        double w = h.Label.Length > 0 ? T(h.Label, h.Color, Fs, FaceLabel).WidthIncludingTrailingWhitespace + 6 * Scale : 0;
        foreach (var c in h.Cells) w += CellW(c) + 6 * Scale;
        return Math.Max(0, w - 6 * Scale);   // no trailing space: the gap (and its bar) follows
    }

    private Size MeasureHorizontal()
    {
        _hitems = BuildHorizontal();
        double w = Pad * 2;
        foreach (var h in _hitems) w += HItemW(h) + Gap;
        if (_hitems.Count > 0) w -= Gap;
        return new Size(Math.Ceiling(w), Math.Ceiling(RowH + Pad * 2));
    }

    private void RenderHorizontal(DrawingContext dc)
    {
        double x = Pad, y = Pad;
        double barW = Math.Max(1, 1.5 * Scale), barH = RowH * 0.45;
        for (int i = 0; i < _hitems.Count; i++)
        {
            var h = _hitems[i];
            double start = x;
            if (h.Graph) DrawGraph(dc, new Rect(x, y + RowH * 0.12, HGraphW, RowH * 0.76));
            else
            {
                if (h.Label.Length > 0)
                {
                    var lab = T(h.Label, h.Color, Fs, FaceLabel);
                    dc.DrawText(lab, new Point(x, y + (RowH - lab.Height) / 2));
                    x += lab.WidthIncludingTrailingWhitespace + 6 * Scale;
                }
                foreach (var c in h.Cells)
                {
                    double cw = CellW(c);
                    DrawCellRight(dc, c, x + cw, y);
                    x += cw + 6 * Scale;
                }
            }
            x = start + HItemW(h);
            if (i < _hitems.Count - 1)
                dc.DrawRectangle(DividerBrush, null, new Rect(Math.Round(x + Gap / 2 - barW / 2), y + (RowH - barH) / 2, barW, barH));
            x += Gap;
        }
    }

    // ---------- frametime graph ----------

    private double GraphMin()
    {
        if (GraphCount == 0) return 0;
        float m = float.MaxValue;
        for (int i = 0; i < GraphCount; i++) m = Math.Min(m, Graph[i]);
        return m;
    }

    private double GraphMax()
    {
        float m = 0;
        for (int i = 0; i < GraphCount; i++) m = Math.Max(m, Graph[i]);
        return m;
    }

    private void DrawGraph(DrawingContext dc, Rect r)
    {
        if (GraphCount < 2) return;
        // MangoHud's default frame_timing graph: a fixed 0 to 50 ms scale (dynamic_frame_timing is off on SteamOS),
        // so a steady 17 ms sits flat a third of the way up and only real stutters spike; slower frames clip at the top
        const double lo = 0, hi = 50;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            for (int i = 0; i < GraphCount; i++)
            {
                double x = r.Left + r.Width * i / (GraphCount - 1);
                double y = r.Bottom - r.Height * (Math.Clamp(Graph[i], lo, hi) - lo) / (hi - lo);
                if (i == 0) c.BeginFigure(new Point(x, y), false, false);
                else c.LineTo(new Point(x, y), true, false);
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, GraphPen, g);
    }
}
