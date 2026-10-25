using System;
using System.Collections.Generic;

namespace AlienControl.Overlay;

/// <summary>ov.15: a colour for the overlay canvases (WPF and Direct2D), straight alpha.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba Hex(uint rgb, byte a = 255) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, a);
}

/// <summary>ov.15: what the overlay draws on. Units are DIPs; y of Text is the top of the line box (like WPF's
/// DrawText), Height / Baseline are the line box height and the baseline's distance from its top.</summary>
public interface IOverlayCanvas
{
    double TextWidth(string s, double size, bool bold);
    (double Height, double Baseline) LineMetrics(string s, double size, bool bold);
    void Text(string s, double x, double y, double size, bool bold, Rgba c);
    void Fill(double x, double y, double w, double h, Rgba c);
    /// <summary>One open polyline (the frametime graph). pts holds x0, y0, x1, y1, ...</summary>
    void Polyline(ReadOnlySpan<double> pts, Rgba c, double width);
}

/// <summary>
/// beta1.3-ov: the MangoHud HUD as a layout that knows nothing about WPF. ov.15 split it out of OverlayPanel so the
/// Direct2D overlay window and the WPF one (preview on the Overlay tab, and the fallback) draw exactly the same thing.
/// Look copied from MangoHud defaults: background #020202 at 50 %, square corners, white text, labels in the MangoHud
/// colours, units in a smaller font, frametime graph in green.
/// Measure once per number update (Measure), draw every frame (Render): the graph is the only part that moves.
/// </summary>
public sealed class OverlayView
{
    // MangoHud default colours (src/overlay_params.h)
    public static readonly Rgba White = new(0xFF, 0xFF, 0xFF);
    internal static readonly Rgba GpuC = new(0x2E, 0x97, 0x62);
    internal static readonly Rgba CpuC = new(0x2E, 0x97, 0xCB);
    internal static readonly Rgba VramC = new(0xAD, 0x64, 0xC1);
    internal static readonly Rgba RamC = new(0xC2, 0x66, 0x93);
    internal static readonly Rgba EngineC = new(0xEB, 0x5B, 0x5B);
    internal static readonly Rgba BattC = new(0xFF, 0x90, 0x78);
    internal static readonly Rgba FanC = new(0xE0, 0x7B, 0x85);
    internal static readonly Rgba CoreE = new(0x7F, 0xB8, 0xDB);   // E cores: a lighter CPU blue
    internal static readonly Rgba DimC = new(0xA0, 0xA0, 0xA0);
    internal static readonly Rgba GraphC = new(0x00, 0xFF, 0x00);
    internal static readonly Rgba DividerC = new(0xFF, 0xFF, 0xFF, 0x73);   // white at 45 %
    // ov.15 temps (Amine): green / yellow / red, red brighter than the first pick (#B22222 was too dark over games)
    internal static readonly Rgba TempOk = Rgba.Hex(0x39F900);
    internal static readonly Rgba TempWarm = Rgba.Hex(0xFDFD09);
    internal static readonly Rgba TempHot = Rgba.Hex(0xFF3B3B);
    internal static readonly Rgba StutterC = Rgba.Hex(0xFF3B3B);

    /// <summary>ov.15 (Amine): CPU green under 80, yellow 80 to 89, red 90 and up.</summary>
    public static Rgba CpuTempColor(int? t) => t is not int x ? White : x < 80 ? TempOk : x < 90 ? TempWarm : TempHot;
    /// <summary>ov.15 (Amine): GPU green under 75, yellow 75 to 79, red 80 and up (the NVIDIA limit is 85).</summary>
    public static Rgba GpuTempColor(int? t) => t is not int x ? White : x < 75 ? TempOk : x < 80 ? TempWarm : TempHot;

    // ---------- inputs ----------
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
    public string Engine { get; set; } = "";
    public double Low1 { get; set; }
    public double Low01 { get; set; }
    public double MaxMs { get; set; }
    /// <summary>ov.6: PresentMon's PresentMode string, empty when unknown.</summary>
    public string PresentMode { get; set; } = "";
    /// <summary>ov.15: the clock row's text (HH:mm), set at each number update.</summary>
    public string Clock { get; set; } = "";
    /// <summary>ov.15: the bound estimate (GPU, CPU, LIMITED, or empty while unknown).</summary>
    public string Bound { get; set; } = "";
    /// <summary>ov.15: stutters in the last ~10 s (frames over 2x the median, generated frames ignored) and the worst one.</summary>
    public int Stutters { get; set; }
    public double StutterWorstMs { get; set; }
    /// <summary>ov.15: frames above this get a red mark on the graph (2x the median), 0 = no marks.</summary>
    public double SpikeMs { get; set; }

    /// <summary>The frametime graph, oldest first: each frame's time on PresentMon's clock (s) and its frametime (ms).
    /// ov.15: plotted by time, the right edge is GraphNow (the playhead), GraphSpan seconds wide, so it scrolls at a
    /// constant speed instead of one step per frame.</summary>
    public double[] GraphT { get; set; } = Array.Empty<double>();
    public float[] Graph { get; set; } = Array.Empty<float>();
    public int GraphCount { get; set; }
    public double GraphNow { get; set; }
    public const double GraphSpan = 2.0;
    /// <summary>The graph's min / max label, set at each number update (not per frame).</summary>
    public string GraphRange { get; set; } = "";

    private bool ShowGen => (Items & Item.FrameGen) != 0 && GenFps >= 1;
    private string LatLabel => LatPc ? "PC LAT" : "LATENCY";
    private string LatValue => double.IsNaN(LatMs) ? "--" : LatMs.ToString(LatMs < 10 ? "0.0" : "0");

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

    private double Fs => 24 * Scale;            // MangoHud font_size 24
    private double FsUnit => Fs * 0.55;         // units are drawn smaller
    private double Pad => 6 * Scale;
    private double RowH => Fs * 1.18;

    // ---------- layout model ----------

    /// <summary>One value cell: the number, its unit, and its colour (temps are coloured, the rest white).</summary>
    internal readonly record struct Cell(string Value, string Unit, Rgba Color)
    {
        public Cell(string value, string unit) : this(value, unit, White) { }
    }
    internal readonly record struct Row(string Label, Rgba Color, Cell[] Cells);

    internal List<Row> BuildRows()
    {
        var d = Data;
        var it = Items;
        var rows = new List<Row>();
        if ((it & Item.Clock) != 0) rows.Add(new("TIME", DimC, new[] { new Cell(Clock.Length > 0 ? Clock : "--", "") }));   // ov.15
        if ((it & (Item.Gpu | Item.GpuTemp | Item.GpuPower | Item.GpuClocks)) != 0)
        {
            var c = new List<Cell>();
            if (d.GpuOff) c.Add(new("OFF", ""));
            else
            {
                if ((it & Item.Gpu) != 0) c.Add(new(N(d.GpuLoad), "%"));
                if ((it & Item.GpuTemp) != 0) c.Add(new(N(d.GpuTemp), "°C", GpuTempColor(d.GpuTemp)));
                if ((it & Item.GpuPower) != 0) c.Add(new(N(d.GpuW), "W"));
                if ((it & Item.GpuClocks) != 0) { c.Add(new(N(d.GpuCoreMhz), "MHz")); c.Add(new(N(d.GpuMemMhz), "MHz")); }
            }
            rows.Add(new("GPU", GpuC, c.ToArray()));
        }
        if ((it & (Item.Cpu | Item.CpuTemp | Item.CpuPower | Item.CpuMhz)) != 0)
        {
            var c = new List<Cell>();
            if ((it & Item.Cpu) != 0) c.Add(new(N(d.CpuLoad), "%"));
            if ((it & Item.CpuTemp) != 0) c.Add(new(N(d.CpuTemp), "°C", CpuTempColor(d.CpuTemp)));
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
        if ((it & Item.Bound) != 0)   // ov.15: an estimate, so it says so
            rows.Add(new("BOUND", DimC, Bound switch
            {
                "LIMITED" => new[] { new Cell("", ""), new Cell("LIMITED", "", TempWarm) },
                "" => new[] { new Cell("--", "") },
                var b => new[] { new Cell(b, ""), new Cell("EST", "") },
            }));
        if ((it & Item.Latency) != 0) rows.Add(new(LatLabel, DimC, new[] { new Cell(LatValue, "ms") }));
        if ((it & Item.Stutter) != 0)   // ov.15: count and the worst one, red when there was one
            rows.Add(new("STUTTER", DimC, Stutters > 0
                ? new[] { new Cell(Stutters.ToString(), ""), new Cell(StutterWorstMs.ToString("0.0"), "ms", StutterC) }
                : new[] { new Cell("0", "") }));
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

    /// <summary>The size of the last Measure, in DIPs.</summary>
    public (double W, double H) Size { get; private set; }

    /// <summary>Lays out the rows for the current values. Call after the numbers change, not every frame.</summary>
    public (double W, double H) Measure(IOverlayCanvas c)
    {
        Size = Level switch
        {
            1 => MeasureL1(c),
            2 => MeasureHorizontal(c),
            >= 3 => MeasureVertical(c),
            _ => (0, 0),
        };
        return Size;
    }

    /// <summary>Draws the last measured layout (background, rows, graph). Cheap enough for every frame.</summary>
    public void Render(IOverlayCanvas c)
    {
        if (Level <= 0 || Size.W <= 0) return;
        c.Fill(0, 0, Size.W, Size.H, new Rgba(2, 2, 2, (byte)Math.Round(Math.Clamp(BgAlpha, 0, 1) * 255)));
        if (Level == 1) RenderL1(c);
        else if (Level == 2) RenderHorizontal(c);
        else RenderVertical(c);
    }

    public bool HasGraph => Level >= 2 && (Items & Item.FrameGraph) != 0;

    private double CellW(IOverlayCanvas c, Cell x) =>
        c.TextWidth(x.Value, Fs, false) + (x.Unit.Length > 0 ? c.TextWidth(x.Unit, FsUnit, false) + 2 * Scale : 0);

    private void DrawCellRight(IOverlayCanvas c, Cell x, double right, double y)
    {
        if (x.Value.Length == 0) return;
        var (vh, vb) = c.LineMetrics(x.Value, Fs, false);
        double r = right;
        if (x.Unit.Length > 0)
        {
            var (_, ub) = c.LineMetrics(x.Unit, FsUnit, false);
            r -= c.TextWidth(x.Unit, FsUnit, false);
            double baseY = y + (RowH - vh) / 2 + vb;
            c.Text(x.Unit, r, baseY - ub, FsUnit, false, x.Color);
            r -= 2 * Scale;
        }
        r -= c.TextWidth(x.Value, Fs, false);
        c.Text(x.Value, r, y + (RowH - vh) / 2, Fs, false, x.Color);
    }

    private void Label(IOverlayCanvas c, string s, Rgba col, double x, double y)
    {
        var (h, _) = c.LineMetrics(s, Fs, true);
        c.Text(s, x, y + (RowH - h) / 2, Fs, true, col);
    }

    // level 1: the fps number alone (MangoHud fps_only); ov.5: then the 1 % low with a small "1%" unit (Amine)
    private bool L1Low => (Items & Item.Low1) != 0;
    private Cell L1LowCell => new(Low1.ToString("0"), "1%");
    private double _l1FpsW;

    private (double, double) MeasureL1(IOverlayCanvas c)
    {
        _l1FpsW = Math.Max(c.TextWidth(Fps.ToString("0"), Fs, false), c.TextWidth("000", Fs, false));
        double w = _l1FpsW + Pad * 2;
        if (L1Low) w += 10 * Scale + Math.Max(CellW(c, L1LowCell), CellW(c, new("000", "1%")));
        return (Math.Ceiling(w), RowH + Pad * 2);
    }

    private void RenderL1(IOverlayCanvas c)
    {
        DrawCellRight(c, new Cell(Fps.ToString("0"), ""), Pad + _l1FpsW, Pad);
        if (L1Low) DrawCellRight(c, L1LowCell, Size.W - Pad, Pad);
    }

    // ---------- vertical (levels 3 and 4) ----------

    private const int ValueCols = 5;
    private readonly double[] _colW = new double[ValueCols];
    private double _labelW;
    private List<Row> _rows = new();
    private double GraphW => 260 * Scale;
    private double GraphH => 46 * Scale;

    private (double, double) MeasureVertical(IOverlayCanvas c)
    {
        _rows = BuildRows();
        _labelW = 0;
        Array.Clear(_colW);
        double minCol = c.TextWidth("100", Fs, false) + c.TextWidth("MHz", FsUnit, false);
        foreach (var r in _rows)
        {
            _labelW = Math.Max(_labelW, c.TextWidth(r.Label, Fs, true));
            for (int i = 0; i < r.Cells.Length && i < ValueCols; i++)
                _colW[i] = Math.Max(_colW[i], Math.Max(CellW(c, r.Cells[i]), minCol));
        }
        double w = Pad * 2 + _labelW;
        foreach (var cw in _colW) if (cw > 0) w += cw + 10 * Scale;
        double h = Pad * 2 + _rows.Count * RowH;
        if ((Items & Item.FrameGraph) != 0)
        {
            w = Math.Max(w, GraphW + Pad * 2);
            h += GraphH + FsUnit * 1.4 + Pad;
        }
        return (Math.Ceiling(w), Math.Ceiling(h));
    }

    private void RenderVertical(IOverlayCanvas c)
    {
        double y = Pad;
        foreach (var r in _rows)
        {
            Label(c, r.Label, r.Color, Pad, y);
            double x = Pad + _labelW;
            for (int i = 0; i < ValueCols; i++)
            {
                if (_colW[i] <= 0) break;
                x += _colW[i] + 10 * Scale;
                if (i < r.Cells.Length) DrawCellRight(c, r.Cells[i], x, y);
            }
            y += RowH;
        }
        if ((Items & Item.FrameGraph) != 0)
        {
            y += Pad / 2;
            c.Text("Frametime", Pad, y, FsUnit, false, White);
            if (GraphRange.Length > 0) c.Text(GraphRange, Size.W - Pad - c.TextWidth(GraphRange, FsUnit, false), y, FsUnit, false, DimC);
            y += FsUnit * 1.4;
            DrawGraph(c, Pad, y, Size.W - Pad * 2, GraphH);
        }
    }

    // ---------- horizontal (level 2) ----------

    internal readonly record struct HItem(string Label, Rgba Color, Cell[] Cells, bool Graph);
    private List<HItem> _hitems = new();

    internal List<HItem> BuildHorizontal()
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
        if ((it & (Item.Cpu | Item.CpuTemp)) != 0)
        {
            var c = new List<Cell>();
            if ((it & Item.Cpu) != 0) c.Add(new(N(d.CpuLoad), "%"));
            if ((it & Item.CpuTemp) != 0) c.Add(new(N(d.CpuTemp), "°C", CpuTempColor(d.CpuTemp)));   // ov.15 (Amine)
            if ((it & Item.CpuPower) != 0) c.Add(new(N(d.CpuW), "W"));
            l.Add(new("CPU", CpuC, c.ToArray(), false));
        }
        if ((it & (Item.Gpu | Item.GpuTemp)) != 0)
        {
            var c = new List<Cell>();
            if (d.GpuOff) c.Add(new("OFF", ""));
            else
            {
                if ((it & Item.Gpu) != 0) c.Add(new(N(d.GpuLoad), "%"));
                if ((it & Item.GpuTemp) != 0) c.Add(new(N(d.GpuTemp), "°C", GpuTempColor(d.GpuTemp)));   // ov.15 (Amine)
                if ((it & Item.GpuPower) != 0) c.Add(new(N(d.GpuW), "W"));
            }
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
    private readonly List<double> _hw = new();

    private double HItemW(IOverlayCanvas c, HItem h)
    {
        if (h.Graph) return HGraphW;
        double w = h.Label.Length > 0 ? c.TextWidth(h.Label, Fs, true) + 6 * Scale : 0;
        foreach (var x in h.Cells) w += CellW(c, x) + 6 * Scale;
        return Math.Max(0, w - 6 * Scale);   // no trailing space: the gap (and its bar) follows
    }

    private (double, double) MeasureHorizontal(IOverlayCanvas c)
    {
        _hitems = BuildHorizontal();
        _hw.Clear();
        double w = Pad * 2;
        foreach (var h in _hitems) { double iw = HItemW(c, h); _hw.Add(iw); w += iw + Gap; }
        if (_hitems.Count > 0) w -= Gap;
        return (Math.Ceiling(w), Math.Ceiling(RowH + Pad * 2));
    }

    private void RenderHorizontal(IOverlayCanvas c)
    {
        double x = Pad, y = Pad;
        double barW = Math.Max(1, 1.5 * Scale), barH = RowH * 0.45;
        for (int i = 0; i < _hitems.Count; i++)
        {
            var h = _hitems[i];
            double start = x;
            if (h.Graph) DrawGraph(c, x, y + RowH * 0.12, HGraphW, RowH * 0.76);
            else
            {
                if (h.Label.Length > 0)
                {
                    Label(c, h.Label, h.Color, x, y);
                    x += c.TextWidth(h.Label, Fs, true) + 6 * Scale;
                }
                foreach (var cell in h.Cells)
                {
                    double cw = CellW(c, cell);
                    DrawCellRight(c, cell, x + cw, y);
                    x += cw + 6 * Scale;
                }
            }
            x = start + _hw[i];
            if (i < _hitems.Count - 1)
                c.Fill(Math.Round(x + Gap / 2 - barW / 2), y + (RowH - barH) / 2, barW, barH, DividerC);
            x += Gap;
        }
    }

    // ---------- frametime graph ----------

    private double[] _pts = new double[1024];

    /// <summary>MangoHud's default frame_timing graph: a fixed 0 to 50 ms scale, so a steady 17 ms sits flat a third of
    /// the way up and only real stutters spike; slower frames clip at the top.
    /// ov.15: x is time, not the frame's index. The right edge is GraphNow, GraphSpan seconds back is the left edge,
    /// and positions are not rounded, so the line slides at a steady speed whatever the frame rate. Frames over SpikeMs
    /// get a red mark above them.</summary>
    private void DrawGraph(IOverlayCanvas c, double left, double top, double width, double height)
    {
        int n = GraphCount;
        if (n < 2) return;
        const double lo = 0, hi = 50;
        double right = left + width, bottom = top + height, now = GraphNow;
        // the first frame drawn is the last one left of the window, so the line runs in from the left edge
        int first = n - 1;
        while (first > 0 && now - GraphT[first] < GraphSpan) first--;
        int count = n - first;
        if (count < 2) return;
        if (_pts.Length < count * 2) _pts = new double[count * 2 + 256];
        int k = 0;
        for (int i = first; i < n; i++)
        {
            double x = right - (now - GraphT[i]) / GraphSpan * width;
            if (x < left) x = left;
            if (x > right) x = right;
            _pts[k++] = x;
            _pts[k++] = bottom - height * (Math.Clamp(Graph[i], lo, hi) - lo) / (hi - lo);
        }
        c.Polyline(_pts.AsSpan(0, k), GraphC, Math.Max(1, 1.4 * Scale));
        if (SpikeMs <= 0) return;
        double mw = Math.Max(2, 3 * Scale), mh = Math.Max(2, 3 * Scale);
        for (int i = first; i < n; i++)
        {
            if (Graph[i] <= SpikeMs) continue;
            double x = right - (now - GraphT[i]) / GraphSpan * width;
            if (x < left || x > right) continue;
            double y = bottom - height * (Math.Clamp(Graph[i], lo, hi) - lo) / (hi - lo);
            c.Fill(x - mw / 2, Math.Max(top, y - mh - 2 * Scale), mw, mh, StutterC);
        }
    }
}
