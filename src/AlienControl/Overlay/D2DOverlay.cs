using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DXGI.AlphaMode;

namespace AlienControl.Overlay;

/// <summary>
/// ov.15: the overlay drawn by the GPU. A Direct3D 11 device, a composition swapchain (premultiplied alpha), Direct2D
/// and DirectWrite on top, and DirectComposition putting the swapchain on the window, so DWM shows each frame
/// directly: no CPU copy of the whole picture per frame (what WPF's transparent window does), and each Present lands
/// on the display's refresh tick. Still a separate window over the game (no injection, anti cheat safe).
///
/// Everything here lives on the render thread that created it. Device lost (driver reset, GPU switched, sleep) is
/// reported by Present / EndDraw; the owner throws this away and creates a new one.
/// </summary>
internal sealed class D2DOverlay : IDisposable
{
    private readonly ID3D11Device _d3d;
    private readonly IDXGIDevice _dxgi;
    private readonly IDXGISwapChain1 _swap;
    private readonly ID2D1Factory1 _f;
    private readonly ID2D1Device _d2d;
    private readonly ID2D1DeviceContext _ctx;
    private readonly IDCompositionDevice _dc;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;
    private readonly D2DCanvas _canvas;
    private ID2D1Bitmap1? _bitmap;
    private int _w, _h;
    private float _dpi = 96;

    /// <summary>Which GPU the device was made on, for the log.</summary>
    public string Adapter { get; }

    public D2DOverlay(IntPtr hwnd, int width, int height)
    {
        _w = Math.Max(8, width); _h = Math.Max(8, height);
        var levels = new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1, Vortice.Direct3D.FeatureLevel.Level_10_0 };
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var d3d).CheckError();
        _d3d = d3d!;
        _dxgi = _d3d.QueryInterface<IDXGIDevice>();
        try { using var d1 = _dxgi.QueryInterface<IDXGIDevice1>(); d1.MaximumFrameLatency = 1; }   // one frame queued: no lag behind the game
        catch (Exception) { /* old runtime: default latency */ }
        using (var adapter = _dxgi.GetAdapter()) Adapter = adapter.Description.Description;
        using (var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false))
        {
            var desc = new SwapChainDescription1((uint)_w, (uint)_h, Format.B8G8R8A8_UNorm, false, Usage.RenderTargetOutput, 2,
                Scaling.Stretch, SwapEffect.FlipSequential, AlphaMode.Premultiplied, SwapChainFlags.None);
            _swap = factory.CreateSwapChainForComposition(_dxgi, desc, null);
        }
        _f = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded, DebugLevel.None);
        _d2d = _f.CreateDevice(_dxgi);
        _ctx = _d2d.CreateDeviceContext(DeviceContextOptions.None);
        _ctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;   // ClearType needs an opaque target
        _ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        CreateTarget();
        _dc = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgi);
        _dc.CreateTargetForHwnd(hwnd, true, out var target).CheckError();
        _target = target!;
        _visual = _dc.CreateVisual();
        _visual.SetContent(_swap).CheckError();
        _target.SetRoot(_visual).CheckError();
        _dc.Commit().CheckError();
        _canvas = new D2DCanvas(_f, _ctx);
    }

    private void CreateTarget()
    {
        _bitmap?.Dispose();
        _ctx.Target = null;
        using var surface = _swap.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            _dpi, _dpi, BitmapOptions.Target | BitmapOptions.CannotDraw);
        _bitmap = _ctx.CreateBitmapFromDxgiSurface(surface, props);
        _ctx.Target = _bitmap;
        _ctx.SetDpi(_dpi, _dpi);
    }

    /// <summary>Size in pixels and the DPI the HUD's DIPs map to. Resizes the swapchain only when something changed.</summary>
    public void Resize(int width, int height, float dpi)
    {
        width = Math.Max(8, width); height = Math.Max(8, height);
        if (width == _w && height == _h && Math.Abs(dpi - _dpi) < 0.01f) return;
        _dpi = dpi;
        if (width != _w || height != _h)
        {
            _ctx.Target = null;
            _bitmap?.Dispose();
            _bitmap = null;
            _w = width; _h = height;
            _swap.ResizeBuffers(2, (uint)_w, (uint)_h, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        }
        CreateTarget();
    }

    /// <summary>The canvas for this frame's draw calls (valid between BeginDraw and EndDraw).</summary>
    public IOverlayCanvas Canvas => _canvas;

    /// <summary>Measuring outside a frame (layout after new numbers). Same text cache as drawing.</summary>
    public IOverlayCanvas Measurer => _canvas;

    public void BeginDraw()
    {
        _ctx.BeginDraw();
        _ctx.Clear(new Color4(0, 0, 0, 0));
    }

    /// <summary>Ends the frame and presents it on the next refresh (blocks until the queue has room: this is what
    /// paces the render loop at the display's rate). False = device lost, make a new D2DOverlay.</summary>
    public bool EndDrawAndPresent()
    {
        try
        {
            var r = _ctx.EndDraw();
            if (r.Failure) { Services.Log.Info("Overlay", $"EndDraw 0x{r.Code:X8}"); return false; }
            var p = _swap.Present(1, PresentFlags.None);
            if (p.Failure) { Services.Log.Info("Overlay", $"Present 0x{p.Code:X8}"); return false; }
            return true;
        }
        catch (SharpGen.Runtime.SharpGenException ex) { Services.Log.Info("Overlay", $"draw failed 0x{ex.ResultCode.Code:X8}"); return false; }
    }

    public void Dispose()
    {
        _canvas.Dispose();
        try { _ctx.Target = null; } catch (Exception) { /* device gone */ }
        _bitmap?.Dispose();
        _visual.Dispose();
        _target.Dispose();
        _dc.Dispose();
        _ctx.Dispose();
        _d2d.Dispose();
        _f.Dispose();
        _swap.Dispose();
        _dxgi.Dispose();
        _d3d.Dispose();
    }

    /// <summary>IOverlayCanvas on Direct2D / DirectWrite. Text layouts are cached per string (numbers change a few times
    /// a second, the graph every frame), brushes per colour.</summary>
    private sealed class D2DCanvas : IOverlayCanvas, IDisposable
    {
        private readonly ID2D1Factory1 _f;
        private readonly ID2D1DeviceContext _ctx;
        private readonly IDWriteFactory _dw;
        private readonly string _family, _monoFamily;
        /// <summary>beta3: the Metal level draws in a monospace face.</summary>
        public bool Mono { get; set; }
        private readonly Dictionary<(float, bool, bool), IDWriteTextFormat> _formats = new();
        /// <summary>A text's layout with its metrics read once (reading them allocates).</summary>
        private sealed record Laid(IDWriteTextLayout Layout, double Width, double Height, double Baseline);
        private readonly Dictionary<(string, float, bool, bool), Laid> _layouts = new();
        private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();

        public D2DCanvas(ID2D1Factory1 f, ID2D1DeviceContext ctx)
        {
            _f = f; _ctx = ctx;
            _dw = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
            // "Ubuntu, Segoe UI" like the WPF overlay: Ubuntu when it's installed (MangoHud's font), Segoe UI otherwise
            using var fonts = _dw.GetSystemFontCollection(false);
            _family = fonts.FindFamilyName("Ubuntu", out _) ? "Ubuntu" : "Segoe UI";
            _monoFamily = fonts.FindFamilyName("Cascadia Mono", out _) ? "Cascadia Mono" : "Consolas";
        }

        private IDWriteTextFormat Format(float size, bool bold)
        {
            if (_formats.TryGetValue((size, bold, Mono), out var f)) return f;
            if (_formats.Count >= 8) { foreach (var x in _formats.Values) x.Dispose(); _formats.Clear(); }   // overlay resized a lot
            f = Mono
                ? _dw.CreateTextFormat(_monoFamily, bold ? FontWeight.Bold : FontWeight.SemiBold,   // beta3.1 (Amine): fat like the Metal HUD
                    Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size)
                : _dw.CreateTextFormat(_family, bold ? FontWeight.Bold : FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size);
            f.WordWrapping = WordWrapping.NoWrap;
            _formats[(size, bold, Mono)] = f;
            return f;
        }

        private Laid Layout(string s, double size, bool bold)
        {
            var key = (s, (float)size, bold, Mono);
            if (_layouts.TryGetValue(key, out var l)) return l;
            if (_layouts.Count > 600)   // old numbers: drop them all, the next frames re-make what's shown
            {
                foreach (var x in _layouts.Values) x.Layout.Dispose();
                _layouts.Clear();
            }
            var t = _dw.CreateTextLayout(s, Format((float)size, bold), float.MaxValue, float.MaxValue);
            var lines = t.LineMetrics;
            l = new Laid(t, t.Metrics.WidthIncludingTrailingWhitespace,
                lines.Length > 0 ? lines[0].Height : size * 1.2, lines.Length > 0 ? lines[0].Baseline : size);
            _layouts[key] = l;
            return l;
        }

        private ID2D1SolidColorBrush Brush(Rgba c)
        {
            uint key = (uint)c.A << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;
            if (_brushes.TryGetValue(key, out var b)) return b;
            b = _ctx.CreateSolidColorBrush(new Color4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f), null);
            _brushes[key] = b;
            return b;
        }

        public double TextWidth(string s, double size, bool bold) => s.Length == 0 ? 0 : Layout(s, size, bold).Width;

        public (double Height, double Baseline) LineMetrics(string s, double size, bool bold)
        {
            var l = Layout(s.Length == 0 ? " " : s, size, bold);
            return (l.Height, l.Baseline);
        }

        public void Text(string s, double x, double y, double size, bool bold, Rgba c)
        {
            if (s.Length == 0) return;
            _ctx.DrawTextLayout(new Vector2((float)x, (float)y), Layout(s, size, bold).Layout, Brush(c), DrawTextOptions.None);
        }

        public void Fill(double x, double y, double w, double h, Rgba c)
        {
            if (w <= 0 || h <= 0) return;
            _ctx.FillRectangle(new Vortice.RawRectF((float)x, (float)y, (float)(x + w), (float)(y + h)), Brush(c));
        }

        public void FillRounded(double x, double y, double w, double h, double r, Rgba c)
        {
            if (w <= 0 || h <= 0) return;
            _ctx.FillRoundedRectangle(new RoundedRectangle(new Vortice.RawRectF((float)x, (float)y, (float)(x + w), (float)(y + h)), (float)r, (float)r), Brush(c));
        }

        private ID2D1StrokeStyle? _round;

        public void Polyline(ReadOnlySpan<double> pts, Rgba c, double width)
        {
            if (pts.Length < 4) return;
            _round ??= _f.CreateStrokeStyle(new StrokeStyleProperties { LineJoin = LineJoin.Round, StartCap = CapStyle.Round, EndCap = CapStyle.Round });
            using var g = _f.CreatePathGeometry();
            using (var sink = g.Open())
            {
                sink.BeginFigure(new Vector2((float)pts[0], (float)pts[1]), FigureBegin.Hollow);
                for (int i = 2; i + 1 < pts.Length; i += 2) sink.AddLine(new Vector2((float)pts[i], (float)pts[i + 1]));
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            _ctx.DrawGeometry(g, Brush(c), (float)width, _round);
        }

        public void Dispose()
        {
            foreach (var l in _layouts.Values) l.Layout.Dispose();
            _layouts.Clear();
            foreach (var b in _brushes.Values) b.Dispose();
            _brushes.Clear();
            foreach (var f in _formats.Values) f.Dispose();
            _round?.Dispose();
            _dw.Dispose();
        }
    }
}
