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
    /// <summary>beta3: text in the monospace face (the Metal HUD level) instead of the MangoHud one, until set back.
    /// Canvases without a monospace face ignore it.</summary>
    bool Mono { get => false; set { } }
    /// <summary>beta3.1: a rounded rectangle (the Metal level's panel). Canvases without one fill it square.</summary>
    void FillRounded(double x, double y, double w, double h, double r, Rgba c) => Fill(x, y, w, h, c);
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
    /// <summary>2.0.3 (Amine): display latency (frame start to screen), NaN when unknown. Shown as its own row next to
    /// PC LAT, so dGPU (Reflex) and Optimus (no Reflex) read the same number.</summary>
    public double DispMs { get; set; } = double.NaN;
    private bool ShowDisp => LatPc && !double.IsNaN(DispMs);
    private string DispValue => DispMs.ToString(DispMs < 10 ? "0.0" : "0");
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
    /// <summary>beta2.4: how far the playhead is from the newest drawn frame to the next one (0 to 1), so the per frame
    /// graph slides by subpixels between frames instead of stepping.</summary>
    public double GraphFrac { get; set; }
    /// <summary>beta2.4: frames across the graph, like MangoHud's frametime history.</summary>
    public const int GraphFrames = 200;
    /// <summary>The graph's min / max label, set at each number update (not per frame).</summary>
    public string GraphRange { get; set; } = "";

    private bool ShowGen => (Items & Item.FrameGen) != 0 && GenFps >= 1;
    private string LatLabel => LatPc ? "PC LAT" : "DISP LAT";   // beta2.3: honest label, present to screen only (no Reflex)
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
    private double GapH => RowH * 0.4;   // 2.0.3 (Amine): space between clusters

    // ---------- layout model ----------

    /// <summary>One value cell: the number, its unit, and its colour (temps are coloured, the rest white).</summary>
    internal readonly record struct Cell(string Value, string Unit, Rgba Color)
    {
        public Cell(string value, string unit) : this(value, unit, White) { }
    }
    internal readonly record struct Row(string Label, Rgba Color, Cell[] Cells, bool Gap = false);

    internal List<Row> BuildRows()
    {
        var d = Data;
        var it = Items;
        var rows = new List<Row>();
        // 2.0.3 (Amine): a little space between clusters: time - gpu - cpu + cores - vram + ram - batt - fans -
        // display, bound, lat, mode - fps, lows, graph
        void Space() { if (rows.Count > 0 && !rows[^1].Gap) rows.Add(new("", DimC, Array.Empty<Cell>(), Gap: true)); }
        if ((it & Item.Clock) != 0) rows.Add(new("TIME", DimC, new[] { new Cell(Clock.Length > 0 ? Clock : "--", "") }));   // ov.15
        Space();
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
        Space();
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
        Space();
        if ((it & Item.Vram) != 0) rows.Add(new("VRAM", VramC, new[] { new Cell(G(d.VramGiB), "GiB") }));
        if ((it & Item.Ram) != 0) rows.Add(new("RAM", RamC, new[] { new Cell(G(d.RamGiB), "GiB") }));
        Space();
        if ((it & Item.Battery) != 0)
        {
            var c = new List<Cell> { new(N(d.BatteryPct), "%") };
            if (d.OnAc) c.Add(new("AC", ""));
            else if (d.BatteryW is int w) c.Add(new(w.ToString(), "W"));
            rows.Add(new("BATT", BattC, c.ToArray()));
        }
        Space();
        if ((it & Item.Fans) != 0)
            rows.Add(new("FANS", FanC, new[] { new Cell(N(d.CpuFanRpm), "RPM"), new Cell(N(d.GpuFanRpm), "RPM") }));
        Space();
        if ((it & Item.Refresh) != 0)
        {
            // 2.0.3 (Amine): VRR (Intel, Optimus) or G-SYNC (NVIDIA) next to the Hz, in the same colours as level 2
            var c = new List<Cell> { new(N(d.RefreshHz), "Hz") };
            _gsyncLast = SyncState(d, Fps, _gsyncLast);
            if (SyncCell(d.Panel, _gsyncLast) is Cell sc) c.Add(sc);
            rows.Add(new("DISPLAY", DimC, c.ToArray()));
        }
        if ((it & Item.Bound) != 0)   // ov.15: an estimate, so it says so
            rows.Add(new("BOUND", DimC, Bound switch
            {
                "LIMITED" => new[] { new Cell("", ""), new Cell("LIMITED", "", TempWarm) },
                "" => new[] { new Cell("--", "") },
                var b => new[] { new Cell(b, ""), new Cell("EST", "") },
            }));
        // 2.0.3 (Amine): one LAT row. Display latency always (comparable in Optimus and dGPU), PC latency next to it
        // when the game has Reflex
        if ((it & Item.Latency) != 0) rows.Add(new("LAT", DimC, LatCells()));
        if ((it & Item.PresentMode) != 0) rows.Add(new("MODE", DimC, new[] { new Cell(ModeLabel(PresentMode), "") }));
        Space();
        if ((it & Item.Engine) != 0)
        {
            var c = new List<Cell> { new(Fps.ToString("0"), "FPS") };
            if (ShowGen) c.Add(new("| " + GenFps.ToString("0"), "FG"));
            if ((it & Item.FrameTime) != 0) c.Add(new(FrameMs.ToString("0.0"), "ms"));
            rows.Add(new(string.IsNullOrEmpty(Engine) ? "FPS" : Engine, EngineC, c.ToArray()));
        }
        // ov.5: lows as normal rows, so they line up with the columns above
        if ((it & Item.Low1) != 0)
        {
            var c = new List<Cell> { new(Low1.ToString("0"), "FPS") };
            if ((it & Item.FrameStats) != 0) c.Add(new(MaxMs.ToString("0.0"), "ms max"));
            rows.Add(new("1% LOW", EngineC, c.ToArray()));
        }
        if ((it & Item.Low01) != 0) rows.Add(new("0.1% LOW", EngineC, new[] { new Cell(Low01.ToString("0"), "FPS") }));
        if (rows.Count > 0 && rows[^1].Gap) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    /// <summary>2.0.3 (Amine): the LAT row's cells. With Reflex: display latency, then PC latency ("ms pc"). Without:
    /// the one latency PresentMon gives (present to screen).</summary>
    internal Cell[] LatCells() => ShowDisp
        ? new[] { new Cell(DispValue, "ms disp"), new Cell(LatValue, "ms pc") }
        : new[] { new Cell(LatValue, "ms") };

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
            1 => MeasureHorizontal(c),   // beta3: the old level 2
            2 => MeasureMetal(c),
            >= 3 => MeasureVertical(c),
            _ => (0, 0),
        };
        return Size;
    }

    /// <summary>Draws the last measured layout (background, rows, graph). Cheap enough for every frame.</summary>
    public void Render(IOverlayCanvas c)
    {
        if (Level <= 0 || Size.W <= 0) return;
        var bg = new Rgba(2, 2, 2, (byte)Math.Round(Math.Clamp(BgAlpha, 0, 1) * 255));
        if (Level == 2) c.FillRounded(0, 0, Size.W, Size.H, 12 * Scale, bg);   // beta3.1 (Amine): rounded like the Metal HUD
        else c.Fill(0, 0, Size.W, Size.H, bg);
        if (Level == 1) RenderHorizontal(c);
        else if (Level == 2) RenderMetal(c);
        else RenderVertical(c);
    }

    public bool HasGraph => Level >= 1 && (Items & Item.FrameGraph) != 0;

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
        double h = Pad * 2;
        foreach (var r in _rows) h += r.Gap ? GapH : RowH;
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
            if (r.Gap) { y += GapH; continue; }
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

    // ---------- horizontal (level 1, the old level 2) ----------

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
        if ((it & Item.Latency) != 0) l.Add(new(LatPc ? "PC LAT" : "DISP", DimC, new[] { new Cell(LatValue, "ms") }, false));
        if ((it & Item.Latency) != 0 && ShowDisp) l.Add(new("DISP", DimC, new[] { new Cell(DispValue, "ms") }, false));
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
    /// beta2.4 (Amine's clips: hitches drew as wide triangles, gaps as long straight lines): one step per frame like
    /// MangoHud / RTSS, the last GraphFrames frames across, sliding by GraphFrac between frames so it still moves
    /// smoothly. A hitch is one thin spike and a pause (loading, menu) just stops the line.</summary>
    private void DrawGraph(IOverlayCanvas c, double left, double top, double width, double height) =>
        DrawGraph(c, left, top, width, height, 50, GraphC, 0);

    /// <summary>hi: the top of the scale (ms). spikeMs > 0: segments touching a frame slower than that are drawn again in
    /// red on top (the Metal level).</summary>
    private void DrawGraph(IOverlayCanvas c, double left, double top, double width, double height, double hi, Rgba color, double spikeMs)
    {
        int n = GraphCount;
        if (n < 2) return;
        const double lo = 0;
        double right = left + width, bottom = top + height;
        double step = width / (GraphFrames - 1), shift = Math.Clamp(GraphFrac, 0, 1) * step;
        int first = Math.Max(0, n - GraphFrames - 1);
        int count = n - first;
        if (_pts.Length < count * 2) _pts = new double[count * 2 + 256];
        int k = 0;
        for (int i = first; i < n; i++)
        {
            double x = right - (n - 1 - i) * step - shift;
            if (x < left)
            {
                // clip the segment entering from the left edge instead of drawing past it
                if (i + 1 < n)
                {
                    double x2 = right - (n - 2 - i) * step - shift;
                    if (x2 <= left) continue;
                    double y1 = Math.Clamp(Graph[i], lo, hi), y2 = Math.Clamp(Graph[i + 1], lo, hi);
                    double yl = y1 + (y2 - y1) * (left - x) / (x2 - x);
                    _pts[k++] = left;
                    _pts[k++] = bottom - height * (yl - lo) / (hi - lo);
                }
                continue;
            }
            _pts[k++] = x;
            _pts[k++] = bottom - height * (Math.Clamp(Graph[i], lo, hi) - lo) / (hi - lo);
        }
        if (k < 4) return;
        double lw = Math.Max(1, 1.4 * Scale);
        c.Polyline(_pts.AsSpan(0, k), color, lw);
        if (spikeMs > 0) DrawSpikes(c, k, bottom, height, hi, spikeMs, lw);
        // beta2 (Amine): no red spike marks any more (the stutter estimate wasn't accurate)
    }

    /// <summary>beta3 Metal level: the parts of the drawn line above spikeMs again in red, one short polyline per spike.</summary>
    private void DrawSpikes(IOverlayCanvas c, int k, double bottom, double height, double hi, double spikeMs, double lw)
    {
        double yThr = bottom - height * Math.Min(spikeMs, hi) / hi;
        int start = -1;
        for (int j = 0; j <= k; j += 2)
        {
            bool up = j < k && _pts[j + 1] < yThr;
            if (up && start < 0) start = Math.Max(0, j - 2);
            else if (!up && start >= 0)
            {
                int end = Math.Min(k, j + 2);
                if (end - start >= 4) c.Polyline(_pts.AsSpan(start, end - start), MetalRed, lw);
                start = -1;
            }
        }
    }

    // ---------- Metal HUD (level 2, beta3) ----------
    // Amine's pick (another agent's mock): dark panel, monospace, label left / value right, the Metal HUD's blue frame
    // interval graph with white grid, red spikes. Rows: clock (its tile), resolution + flip mode (latency toggle),
    // VRAM / RAM, G-SYNC, then FPS, 1% low, frame interval + graph, then GPU, CPU, latency (toggle).

    internal static readonly Rgba MetalGreen = Rgba.Hex(0x6EDC5A);
    internal static readonly Rgba MetalBlue = Rgba.Hex(0x4696FF);
    internal static readonly Rgba MetalRed = Rgba.Hex(0xF0463C);
    internal static readonly Rgba MetalGrid = new(0xFF, 0xFF, 0xFF, 0x80);

    /// <summary>beta3.2: one piece of a Metal row on the monospace grid. Right = Col is where the text ends (right
    /// aligned), else where it starts. Col MCols = the panel's right edge.</summary>
    internal readonly record struct MSeg(string Text, Rgba Color, int Col, bool Right);
    internal readonly record struct MRow(MSeg[] Segs, bool Spacer = false, bool Graph = false)
    {
        public string Find(int col) { foreach (var x in Segs) if (x.Col == col) return x.Text; return ""; }
        public string Label => Segs.Length > 0 && Segs[0].Col == 0 && !Segs[0].Right ? Segs[0].Text : "";
    }
    internal const int MCols = 32;   // beta3.4: 2 wider so "2.0GB 14.8GB" keeps a space
    private List<MRow> _mrows = new();
    private double _mGraphHi = 20;
    private bool? _gsyncLast;
    internal static readonly Rgba MetalCpu = Rgba.Hex(0x5AA0FF);
    internal static readonly Rgba IntelAccent = Rgba.Hex(0x00C7FD);    // 2.0.3: Intel energy blue
    internal static readonly Rgba NvidiaAccent = Rgba.Hex(0x76B900);   // 2.0.3: NVIDIA green

    /// <summary>2.0.3 (Amine): the DISPLAY row's sync tag on levels 3 / 4: "G-SYNC" or "VRR" in the accent when on,
    /// dimmed with "OFF" when off, nothing while unknown.</summary>
    internal static Cell? SyncCell(PanelGpu panel, bool? on)
    {
        if (on is not bool o) return null;
        var (text, color) = SyncLabel(panel, o);
        string name = text[..text.LastIndexOf(' ')];
        return o ? new Cell(name, "", color) : new Cell(name, "OFF", color);
    }

    /// <summary>2.0.3 (Amine): the Metal sync row's words and colour for the GPU that drives the panel.</summary>
    internal static (string Text, Rgba Color) SyncLabel(PanelGpu panel, bool? on)
    {
        bool intel = panel == PanelGpu.Intel;
        string name = intel ? "VRR" : "G-SYNC";
        Rgba accent = intel ? IntelAccent : NvidiaAccent;
        return on switch
        {
            true => ($"{name} On", accent),
            false => ($"{name} Off", new Rgba(accent.R, accent.G, accent.B, 0x99)),
            _ => ($"{name} --", DimC),
        };
    }

    private double MFs => 20 * Scale;
    private double MRowH => MFs * 1.22;
    private double MGraphH => 58 * Scale;
    private double MPad => 12 * Scale;   // beta3.1: more room inside the rounded corners

    /// <summary>G-SYNC from what the panel does: the counted refresh (vblanks) follows the game well under the mode's
    /// Hz = on; the counted refresh sits at the mode's Hz while the game runs well under it = off. With the game at or
    /// near the max refresh it can't tell, so the last answer stays (null until the first one).</summary>
    internal static bool? GsyncState(int? countedHz, int? modeHz, double fps, bool? last)
    {
        if (countedHz is not int hz || modeHz is not int mode || mode <= 0 || fps < 1) return last;
        if (fps >= mode - 8) return last;
        if (hz <= mode - 8) return true;
        if (hz >= mode - 3) return false;
        return last;
    }

    /// <summary>2.0.3 (Amine: VRR read Off in Optimus): the driver's answer; the counted-refresh guess only on the
    /// NVIDIA side, since vblank counting on the Intel path doesn't follow VRR, so Intel with no answer stays "--".</summary>
    internal static bool? SyncState(Sensors d, double fps, bool? last) =>
        d.VrrOn ?? (d.Panel == PanelGpu.Intel ? null : GsyncState(d.RefreshHz, d.ModeHz, fps, last));

    private static MRow Lr(string l, Rgba lc, string r, Rgba rc) =>
        new(new[] { new MSeg(l, lc, 0, false), new MSeg(r, rc, MCols, true) });

    /// <summary>beta3.2 (Amine, Metal HUD): "Label:  value [  a   b]" on fixed columns, brackets line up row to row.</summary>
    private static MRow Br(string label, string v, string a, string b, Rgba c, Rgba? ca = null, Rgba? cb = null) =>
        new(new[] { new MSeg(label, c, 0, false), new MSeg(v, c, 15, true), new MSeg("[", c, 16, false),
                    new MSeg(a, ca ?? c, 23, true), new MSeg(b, cb ?? c, 30, true), new MSeg("]", c, 31, false) });

    internal List<MRow> BuildMetal()
    {
        var d = Data; var it = Items;
        var r = new List<MRow>();
        if ((it & Item.Clock) != 0) r.Add(Lr("Time", White, $"[{(Clock.Length > 0 ? Clock : "--:--")}]", White));
        if ((it & Item.Resolution) != 0) r.Add(Lr("Resolution", White, $"[{d.Resolution ?? "--"}]", White));
        if ((it & (Item.Gsync | Item.PresentMode)) != 0)
        {
            string mode = (it & Item.PresentMode) != 0 ? MetalMode(PresentMode) : "";
            string gs = "";
            Rgba gc = DimC;
            if ((it & Item.Gsync) != 0)
            {
                // 2.0.1 (Amine): the driver's answer when it gives one, else the guess from the counted refresh
                _gsyncLast = SyncState(d, Fps, _gsyncLast);
                // 2.0.3 (Amine): VRR in Intel blue when the Intel GPU drives the panel (Optimus), G-SYNC in NVIDIA
                // green otherwise; Off keeps the accent, dimmed
                (gs, gc) = SyncLabel(d.Panel, _gsyncLast);
            }
            r.Add(Lr(mode, MetalGreen, gs, gc));
        }
        if (r.Count > 0) r.Add(new(Array.Empty<MSeg>(), Spacer: true));
        // beta3.5: min / max over the frames the graph shows, from 10 frame averages for the fast end so uneven pacing
        // (0.2 ms / 14 ms pairs) doesn't read as 4651 fps; the slowest frame stays raw so a hitch still shows
        var (mn, mx) = MetalRange(Graph, GraphCount);
        // 2.0.1 (Amine): no game (no frames) = "--" everywhere instead of 0.00
        bool live = Fps > 0;
        bool have = live && mx > 0;
        if ((it & Item.Fps) != 0)
            r.Add(Br("FPS:", live ? Fps.ToString("0.00") : "--", (it & Item.Low1) != 0 ? (live && Low1 > 0 ? Low1.ToString("0.00") : "--") : "",
                have ? Math.Min(9999, 1000 / mn).ToString("0.00") : "--", White, MetalRed, White));
        if ((it & Item.FrameTime) != 0)
            r.Add(Br("Frame:", live && FrameMs > 0 ? FrameMs.ToString("0.00") : "--", have ? mn.ToString("0.00") : "--", have ? mx.ToString("0.00") : "--", MetalBlue, MetalBlue, MetalRed));
        if ((it & Item.Latency) != 0) r.Add(Lr(ShowDisp ? "PC lat:" : "Latency:", White, LatValue == "--" ? "--" : LatValue + "ms", White));
        if ((it & Item.Latency) != 0 && ShowDisp) r.Add(Lr("Disp lat:", White, DispValue + "ms", White));
        if ((it & Item.FrameGraph) != 0) r.Add(new(Array.Empty<MSeg>(), Graph: true));
        if ((it & (Item.Gpu | Item.GpuTemp | Item.GpuPower)) != 0)
            r.Add(d.GpuOff ? Lr("GPU:", MetalGreen, "OFF", MetalGreen)
                : Br("GPU:", (it & Item.Gpu) != 0 ? N(d.GpuLoad) + "%" : "", (it & Item.GpuTemp) != 0 ? N(d.GpuTemp) + "°C" : "",
                    (it & Item.GpuPower) != 0 ? N(d.GpuW) + "W" : "", MetalGreen, d.GpuTemp >= 80 ? MetalRed : MetalGreen));
        if ((it & (Item.Cpu | Item.CpuTemp | Item.CpuPower)) != 0)
            r.Add(Br("CPU:", (it & Item.Cpu) != 0 ? N(d.CpuLoad) + "%" : "", (it & Item.CpuTemp) != 0 ? N(d.CpuTemp) + "°C" : "",
                (it & Item.CpuPower) != 0 ? N(d.CpuW) + "W" : "", MetalCpu, d.CpuTemp >= 90 ? MetalRed : MetalCpu));
        if ((it & (Item.Vram | Item.Ram)) != 0)   // Amine: VRAM and RAM both in the brackets, lined up with GPU / CPU
            r.Add(new(new[] { new MSeg("Mem:", White, 0, false), new MSeg("[", White, 16, false),
                new MSeg((it & Item.Vram) != 0 ? G(d.VramGiB) + "GB" : "", White, 23, true),
                new MSeg((it & Item.Ram) != 0 ? G(d.RamGiB) + "GB" : "", White, 30, true), new MSeg("]", White, 31, false) }));
        return r;
    }

    /// <summary>The flip mode the Metal way (Direct / Composited): iFlip is what G-SYNC wants.</summary>
    internal static string MetalMode(string m) => ModeLabel(m) switch
    {
        "IFLIP" => "iFlip",
        "IFLIP MPO" => "iFlip MPO",
        "FULLSCREEN" => "Fullscreen",
        "FS COPY" => "FS Copy",
        "COMPOSED" => "Composed",
        "COMPOSED COPY" => "Composed Copy",
        _ => "--",
    };

    /// <summary>Fastest 10 frame average and slowest single frame over the last GraphFrames frames (ms).</summary>
    internal static (double Min, double Max) MetalRange(float[] g, int count)
    {
        const int W = 10;
        int end = Math.Min(count, g.Length), start = Math.Max(0, end - GraphFrames);
        Span<float> v = stackalloc float[GraphFrames];
        int n = 0;
        double mx = 0;
        for (int i = start; i < end; i++)
        {
            float f = g[i];
            if (f <= 0 || float.IsNaN(f)) continue;
            v[n++] = f;
            if (f > mx) mx = f;
        }
        if (n == 0) return (0, 0);
        int w = Math.Min(W, n);
        double sum = 0;
        for (int i = 0; i < w; i++) sum += v[i];
        double mn = sum;
        for (int i = w; i < n; i++) { sum += v[i] - v[i - w]; if (sum < mn) mn = sum; }
        return (mn / w, mx);
    }

    private (double, double) MeasureMetal(IOverlayCanvas c)
    {
        _mrows = BuildMetal();
        bool mono = c.Mono;
        c.Mono = true;
        try
        {
            double w = c.TextWidth(new string('0', MCols), MFs, false);
            double h = MPad * 2;
            foreach (var r in _mrows)
                h += r.Spacer ? MRowH * 0.35 : r.Graph ? MGraphH + MRowH * 0.25 : MRowH;
            // graph scale: about 2.5x the average frame interval, in 5 ms steps so it doesn't wobble with every update
            _mGraphHi = Math.Clamp(Math.Ceiling(FrameMs * 4 / 10) * 10, 20, 100);   // beta3.5: room for uneven pacing
            return (Math.Ceiling(w + MPad * 2), Math.Ceiling(h));
        }
        finally { c.Mono = mono; }
    }

    private void RenderMetal(IOverlayCanvas c)
    {
        bool mono = c.Mono;
        c.Mono = true;
        try
        {
            double x = MPad, xr = Size.W - MPad, y = MPad;
            double cw = c.TextWidth("0", MFs, false);
            var (h, _) = c.LineMetrics("0", MFs, false);
            foreach (var r in _mrows)
            {
                if (r.Spacer) { y += MRowH * 0.35; continue; }
                if (r.Graph)
                {
                    y += MRowH * 0.1;
                    DrawMetalGraph(c, x, y, xr - x, MGraphH);
                    y += MGraphH + MRowH * 0.15;
                    continue;
                }
                double ty = y + (MRowH - h) / 2;
                foreach (var sg in r.Segs)
                {
                    if (sg.Text.Length == 0) continue;
                    double end = sg.Col >= MCols ? xr : x + sg.Col * cw;
                    double sx = sg.Right ? end - c.TextWidth(sg.Text, MFs, false) : x + sg.Col * cw;
                    c.Text(sg.Text, sx, ty, MFs, false, sg.Color);
                }
                y += MRowH;
            }
        }
        finally { c.Mono = mono; }
    }

    private void DrawMetalGraph(IOverlayCanvas c, double left, double top, double width, double height)
    {
        double t = Math.Max(1, Scale);
        // box and 6 grid lines, like the Metal HUD
        for (int i = 1; i < 7; i++) c.Fill(Math.Round(left + i * width / 7), top, t, height, MetalGrid);
        c.Fill(left, top, width, t, White);
        c.Fill(left, top + height - t, width, t, White);
        c.Fill(left, top, t, height, White);
        c.Fill(left + width - t, top, t, height, White);
        double spike = FrameMs > 0 ? Math.Max(FrameMs * 2.5, FrameMs + 10) : 0;   // beta3.5: real hitches only, not every other frame of a zigzag
        DrawGraph(c, left + t, top + t, width - 2 * t, height - 2 * t, _mGraphHi, MetalBlue, spike);
    }
}
