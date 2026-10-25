using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AlienControl.Services;

namespace AlienControl.Overlay;

/// <summary>
/// beta1.3-ov: the in-game overlay window on its own UI thread, so a busy app window never stutters it and it never
/// stutters the app. Click-through, never activated, no taskbar button, topmost (works over windowed and borderless
/// games, like RTSS without injection; exclusive fullscreen games draw over it).
///
/// ov.15: two ways to draw the same HUD (OverlayView).
///  - Direct2D (default): a plain Win32 window with DirectComposition content. A render thread draws every refresh
///    while the frametime graph is shown (otherwise only when the numbers change), paced by Present, so the graph
///    scrolls at a constant speed. See D2DOverlay.
///  - WPF (fallback, or "OverlayRenderer": "wpf" in settings.json): the old transparent WPF window; its graph now
///    redraws on CompositionTarget.Rendering instead of a 33 ms timer.
/// Direct2D falls back to WPF by itself when it can't start or loses its device three times in a minute.
/// The numbers update on their own timer (the Update every setting) on the overlay's UI thread.
/// </summary>
public sealed class OverlayHost : IDisposable
{
    private Thread? _thread;
    private Dispatcher? _disp;
    private DispatcherTimer? _timer;
    private readonly FramesSource _frames;
    private volatile Sensors _sensors = new();
    private readonly ManualResetEventSlim _ready = new();

    // settings, written from the app thread, read on the overlay thread
    private volatile int _level, _position, _updateMs = 500, _offX, _offY;
    private double _scale = 0.8, _bg = 0.5;
    private volatile bool _extended, _smooth;
    private Item _hidden;   // ov.9: Show on overlay toggles
    private volatile bool _wantD2D = true;

    // the HUD, shared by the number timer (overlay UI thread) and the D2D render thread: _vl guards it
    private readonly object _vl = new();
    private OverlayView _view = new();
    private bool _dirty = true;

    // WPF renderer
    private Window? _win;
    private OverlayPanel? _panel;
    private bool _renderingHooked;

    // D2D renderer
    private IntPtr _hwnd;
    private Thread? _render;
    private volatile bool _renderRun, _renderShow;
    private readonly AutoResetEvent _wake = new(false);
    private volatile int _pxW, _pxH;
    private static volatile bool _d2dBroken;   // gave up on D2D for this run (couldn't start, or kept losing its device)
    public string Renderer => _hwnd != IntPtr.Zero ? "d2d" : _win is not null ? "wpf" : "none";

    private readonly FramesSource.Frame[] _buf = new FramesSource.Frame[4096];   // ov.15: ~10 s for stutters at 240 fps
    private readonly FramesSource.Frame[] _gbuf = new FramesSource.Frame[1024];  // the graph's 2 s (render thread or WPF)
    private readonly double[] _gT = new double[1024];
    private readonly float[] _gMs = new float[1024];
    private readonly FramesSource.Playhead _play = new();
    private readonly OverlayPerf _perf = new();

    public OverlayHost(FramesSource frames) { _frames = frames; }

    public bool Shown => _level > 0;

    /// <summary>ov.11: raised on the overlay thread once the window and its thread are gone (30 s after off), so the app
    /// can give the memory back.</summary>
    public event Action? TornDown;

    /// <summary>How long the window and its thread stay parked after off before they're torn down. F4 off / on within
    /// this keeps the warm window.</summary>
    public static TimeSpan IdleTeardown { get; set; } = TimeSpan.FromSeconds(30);

    private readonly object _life = new();
    private DispatcherTimer? _idle;

    public void Push(Sensors s) => _sensors = s;

    /// <summary>Applies level and look. Level 0 hides the window; 30 s later the window and its thread are torn down
    /// (ov.11, gives the render memory back). The next level starts them again. renderer: "d2d" or "wpf" (ov.15).</summary>
    public void Apply(int level, int position, double scale, double bg, int updateMs, int offsetX = 0, int offsetY = 0, bool extended = false,
        bool smooth = false, Item hidden = Item.None, string renderer = "d2d")
    {
        _extended = extended;
        _hidden = hidden;
        _smooth = smooth;
        _wantD2D = !string.Equals(renderer, "wpf", StringComparison.OrdinalIgnoreCase);
        _offX = offsetX; _offY = offsetY;
        _level = level; _position = position; _scale = scale; _bg = bg; _updateMs = Math.Clamp(updateMs, 100, 2000);
        if (level > 0) EnsureThread();
        if (_disp is { HasShutdownStarted: false } d) d.BeginInvoke(new Action(ApplyOnThread));
    }

    private void EnsureThread()
    {
        lock (_life)
        {
            if (_thread is { IsAlive: true } && _disp is { HasShutdownStarted: false }) return;
            _thread?.Join(2000);   // a teardown is finishing: let it, then start fresh
            _disp = null;
            _ready.Reset();
            _thread = new Thread(Run) { IsBackground = true, Name = "AlienControls overlay" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(3000);
        }
    }

    /// <summary>On the overlay thread, IdleTeardown after off: close the window, stop the dispatcher. Skipped when a
    /// level came back meanwhile or the app thread is starting a new one (never blocks on it).</summary>
    private void TearDown()
    {
        _idle?.Stop();
        if (_level > 0 || !Monitor.TryEnter(_life)) return;
        try
        {
            if (_level > 0) return;
            _timer?.Stop();
            CloseWindows();
            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            Log.Info("Overlay", "window torn down after idle");
        }
        finally { Monitor.Exit(_life); }
    }

    private void Run()
    {
        try
        {
            _disp = Dispatcher.CurrentDispatcher;
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(_updateMs) };
            _timer.Tick += (_, _) => Tick();
            _idle = new DispatcherTimer(DispatcherPriority.Background) { Interval = IdleTeardown };
            _idle.Tick += (_, _) => TearDown();
            _ready.Set();
            Dispatcher.Run();
            if (_level <= 0) TornDown?.Invoke();
        }
        catch (Exception ex) { Log.Warn("Overlay.Run", ex); _ready.Set(); }
    }

    // ---------- windows ----------

    /// <summary>Makes the window for the wanted renderer if it isn't there (D2D, or WPF when D2D is off or broken).</summary>
    private void EnsureWindow()
    {
        bool d2d = _wantD2D && !_d2dBroken;
        if (d2d && _hwnd != IntPtr.Zero) return;
        if (!d2d && _win is not null) return;
        CloseWindows();
        if (d2d && StartD2D()) return;
        StartWpf();
    }

    private void StartWpf()
    {
        _panel = new OverlayPanel();
        lock (_vl) { _view = _panel.View; _dirty = true; }
        _win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false, Focusable = false, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, Content = _panel, Title = "Alien Controls overlay",
        };
        _win.SourceInitialized += (_, _) => MakeClickThrough(new WindowInteropHelper(_win).Handle);
        _win.SizeChanged += (_, _) => Place();
        Log.Info("Overlay", "renderer: WPF" + (_wantD2D ? " (Direct2D unavailable)" : ""));
    }

    private bool StartD2D()
    {
        try
        {
            _hwnd = CreateOverlayHwnd();
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed " + Marshal.GetLastWin32Error());
            lock (_vl) { _view = new OverlayView(); _dirty = true; }
            using var started = new ManualResetEventSlim();
            string? err = null;
            _renderRun = true;
            _render = new Thread(() => RenderLoop(started, e => err = e)) { IsBackground = true, Name = "AlienControls overlay render", Priority = ThreadPriority.AboveNormal };
            _render.Start();
            if (!started.Wait(3000) || err is not null) throw new InvalidOperationException(err ?? "render thread didn't start");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Overlay.D2D", ex);
            _d2dBroken = true;
            StopD2D();
            return false;
        }
    }

    private void StopD2D()
    {
        _renderRun = false;
        _wake.Set();
        if (_render is { IsAlive: true } r && !r.Join(2000)) Log.Info("Overlay", "render thread didn't stop in time");
        _render = null;
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }

    private void CloseWindows()
    {
        UnhookRendering();
        StopD2D();
        if (_win is not null) { _win.Close(); _win = null; _panel = null; }
    }

    private void ApplyOnThread()
    {
        if (_timer is null) return;
        int level = _level;
        if (level <= 0)
        {
            _timer.Stop();
            UnhookRendering();
            _renderShow = false;
            if (_win is { IsVisible: true }) _win.Hide();
            if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE);
            _perf.Stop();
            if (_idle is { IsEnabled: false }) { _idle.Interval = IdleTeardown; _idle.Start(); }
            return;
        }
        _idle?.Stop();
        EnsureWindow();
        lock (_vl)
        {
            _view.Level = level;
            _view.Items = OverlayLevels.Items(level, _extended, _hidden);
            _view.Scale = _scale;
            _view.BgAlpha = _bg;
            _dirty = true;
        }
        _timer.Interval = TimeSpan.FromMilliseconds(_updateMs);
        Tick();
        _perf.Start(Renderer, DisplayRefresh.Read()?.CurrentHz ?? 60);
        if (_win is not null)
        {
            if (!_win.IsVisible) _win.Show();
            // ov.15: the graph redraws with WPF's own frames (was a 33 ms timer), only while it's shown
            if (_view.HasGraph) HookRendering(); else UnhookRendering();
        }
        if (_hwnd != IntPtr.Zero)
        {
            _renderShow = true;
            _wake.Set();
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        }
        Place();
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void HookRendering()
    {
        if (_renderingHooked) return;
        CompositionTarget.Rendering += OnWpfRendering;
        _renderingHooked = true;
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked) return;
        CompositionTarget.Rendering -= OnWpfRendering;
        _renderingHooked = false;
    }

    /// <summary>WPF renderer: the graph alone, once per WPF frame, no new numbers and no re-measure.</summary>
    private void OnWpfRendering(object? sender, EventArgs e)
    {
        if (_panel is null) return;
        lock (_vl) FillGraph(_view);
        _panel.InvalidateVisual();
        _perf.Frame();
    }

    // ---------- numbers and graph ----------

    private int _topTick;

    private void Tick()
    {
        lock (_vl)
        {
            Fill(_view, _frames, _buf, _sensors);
            FillGraph(_view);
            _dirty = true;
        }
        if (_panel is not null) { _panel.InvalidateMeasure(); _panel.InvalidateVisual(); }
        if (_hwnd != IntPtr.Zero) _wake.Set();
        // games raise themselves on focus; keep the overlay above them (cheap, no activation)
        if (++_topTick % Math.Max(1, 1000 / _updateMs) == 0) Place();
        _perf.MaybeLog();
    }

    /// <summary>The graph's frames up to the playhead (a burst behind, so it scrolls evenly) into the view.</summary>
    private void FillGraph(OverlayView v)
    {
        if (!v.HasGraph) { v.GraphCount = 0; return; }
        int n = _frames.CopyRecent(_gbuf);
        n = _play.Visible(new ReadOnlySpan<FramesSource.Frame>(_gbuf, 0, n), Stopwatch.GetTimestamp());
        int m = 0;   // beta2: real frames only, so frame gen draws one flat line
        for (int i = 0; i < n; i++) { if (_gbuf[i].Gen) continue; _gT[m] = _gbuf[i].T; _gMs[m] = _gbuf[i].Ms; m++; }
        v.GraphT = _gT;
        v.Graph = _gMs;
        v.GraphCount = m;
        v.GraphNow = m > 0 ? Math.Max(_play.Now, _gT[m - 1]) : _play.Now;
    }

    /// <summary>Frame stats into a view. fps over the last 250 ms (ov.14), lows over the newest 2048 frames, stutters
    /// over ~10 s, the graph's min / max over its 2 s, and the ov.15 rows (clock, bound) when shown.</summary>
    internal static void Fill(OverlayView p, FramesSource frames, FramesSource.Frame[] buf, Sensors s)
    {
        int n = frames.CopyRecent(buf);
        var span = new ReadOnlySpan<FramesSource.Frame>(buf, 0, n);
        long now = Stopwatch.GetTimestamp();
        var st = FramesSource.StatsOver(span, now, Stopwatch.Frequency / 4);
        p.Data = s;
        p.Fps = st.Fps;                       // real fps: the game's own frames (= all frames without frame gen)
        p.GenFps = st.GenFps;
        p.FrameMs = st.AvgMs;
        p.LatMs = st.LatMs;
        p.LatPc = st.PcLat;
        var (low1, low01, _) = Lows(span[Math.Max(0, n - 2048)..]);
        p.Low1 = low1;
        p.Low01 = low01;
        // the graph's span: max and the min / max label
        int g = n;
        while (g > 0 && n > 0 && span[n - 1].T - span[g - 1].T <= OverlayView.GraphSpan) g--;
        var shown = span[g..];
        float mn = float.MaxValue, mx = 0;
        foreach (var f in shown) { if (f.Gen) continue; if (f.Ms < mn) mn = f.Ms; if (f.Ms > mx) mx = f.Ms; }
        p.MaxMs = mx > 0 ? mx : 0;
        p.GraphRange = mx > 0 ? $"min {mn:0.0} / max {mx:0.0} ms" : "";
        p.Engine = EngineLabel(frames);
        p.PresentMode = frames.PresentMode;
        if ((p.Items & Item.Bound) != 0) p.Bound = FramesSource.Bound(span, s.LimitFps(frames.SyncInterval));
        if ((p.Items & Item.Clock) != 0) p.Clock = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    [ThreadStatic] private static float[]? _lowScratch;

    /// <summary>1 % and 0.1 % low fps (average of the slowest 1 % / 0.1 % of frames, at least one frame) and the max
    /// frametime. One sort of at most 2048 floats per update, no allocation after the first call.</summary>
    internal static (double Low1, double Low01, double MaxMs) Lows(ReadOnlySpan<FramesSource.Frame> f)
    {
        if (f.Length == 0) return (0, 0, 0);
        var buf = _lowScratch is { } b && b.Length >= f.Length ? b : (_lowScratch = new float[Math.Max(f.Length, 2048)]);
        int k0 = 0;
        for (int i = 0; i < f.Length; i++) if (!f[i].Gen) buf[k0++] = f[i].Ms;   // beta2: real frames only
        if (k0 == 0) return (0, 0, 0);
        var ms = buf.AsSpan(0, k0);
        ms.Sort();
        return (Worst(ms, 100), Worst(ms, 1000), ms[^1]);

        static double Worst(Span<float> sorted, int per)
        {
            int k = Math.Max(1, sorted.Length / per);
            double sum = 0;
            for (int i = sorted.Length - k; i < sorted.Length; i++) sum += sorted[i];
            double avg = sum / k;
            return avg > 0 ? 1000 / avg : 0;
        }
    }

    private static int _engPid;
    private static string _engLabel = "";

    /// <summary>MangoHud's engine label (DX12, VULKAN, ...). From the game's loaded modules, once per game; falls back
    /// to PresentMon's runtime when the process can't be opened (protected by an anti cheat).</summary>
    internal static string EngineLabel(FramesSource frames)
    {
        int pid = frames.TargetPid;
        if (pid == 0) return "";
        if (pid == _engPid) return _engLabel;
        _engPid = pid;
        _engLabel = frames.Runtime switch { "D3D9" => "DX9", "DXGI" => "DXGI", _ => "" };
        try
        {
            using var p = Process.GetProcessById(pid);
            bool d3d12 = false, d3d11 = false, vk = false, gl = false;
            foreach (ProcessModule m in p.Modules)
            {
                var name = m.ModuleName.ToLowerInvariant();
                if (name == "vulkan-1.dll") vk = true;
                else if (name == "d3d12.dll") d3d12 = true;
                else if (name == "d3d11.dll") d3d11 = true;
                else if (name == "opengl32.dll") gl = true;
            }
            // DXVK / VKD3D games load d3d1x shims over Vulkan: Vulkan wins, like MangoHud on Linux shows VULKAN
            _engLabel = vk ? "VULKAN" : d3d12 ? "DX12" : d3d11 ? "DX11" : gl ? "OPENGL" : _engLabel;
        }
        catch (Exception) { /* protected process: keep the runtime label */ }
        return _engLabel;
    }

    // ---------- D2D render thread ----------

    /// <summary>Draws while shown: every refresh when the graph is on screen (Present paces it), otherwise only when the
    /// numbers changed. Rebuilds the device after a loss; three losses in a minute and the overlay goes back to WPF.</summary>
    private void RenderLoop(ManualResetEventSlim started, Action<string> fail)
    {
        D2DOverlay? gfx = null;
        int losses = 0;
        long firstLoss = 0;
        try
        {
            try
            {
                gfx = new D2DOverlay(_hwnd, 64, 32);
                Log.Info("Overlay", $"renderer: Direct2D on {gfx.Adapter}");
            }
            catch (Exception ex) { fail(ex.Message); Log.Warn("Overlay.D2D init", ex); return; }
            finally { started.Set(); }

            while (_renderRun)
            {
                if (!_renderShow) { _wake.WaitOne(500); continue; }
                bool graph;
                bool ok;
                lock (_vl)
                {
                    graph = _view.HasGraph;
                    if (graph) FillGraph(_view);
                    if (_dirty)
                    {
                        _view.Measure(gfx.Measurer);
                        _dirty = false;
                        float dpi = GetDpiForWindow(_hwnd);
                        if (dpi <= 0) dpi = 96;
                        int w = (int)Math.Ceiling(_view.Size.W * dpi / 96), h = (int)Math.Ceiling(_view.Size.H * dpi / 96);
                        gfx.Resize(w, h, dpi);
                        if (w != _pxW || h != _pxH)
                        {
                            _pxW = w; _pxH = h;
                            _disp?.BeginInvoke(new Action(Place));
                        }
                    }
                    gfx.BeginDraw();
                    _view.Render(gfx.Canvas);
                }
                ok = gfx.EndDrawAndPresent();
                if (!ok)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (now - firstLoss > Stopwatch.Frequency * 60) { losses = 0; firstLoss = now; }
                    losses++;
                    Log.Info("Overlay", $"Direct2D device lost ({losses}), rebuilding");
                    try { gfx.Dispose(); } catch (Exception) { /* gone */ }
                    gfx = null;
                    if (losses >= 3) { GiveUpD2D("device lost 3 times in a minute"); return; }
                    Thread.Sleep(250);
                    try { gfx = new D2DOverlay(_hwnd, Math.Max(8, _pxW), Math.Max(8, _pxH)); lock (_vl) _dirty = true; }
                    catch (Exception ex) { Log.Warn("Overlay.D2D rebuild", ex); GiveUpD2D("rebuild failed"); return; }
                    continue;
                }
                _perf.Frame(graph);
                if (!graph) _wake.WaitOne(1000);   // nothing moves until the next numbers
            }
        }
        catch (Exception ex) { Log.Warn("Overlay.Render", ex); GiveUpD2D("render error"); }
        finally { try { gfx?.Dispose(); } catch (Exception) { /* device gone */ } }
    }

    private void GiveUpD2D(string why)
    {
        _d2dBroken = true;
        Log.Info("Overlay", $"Direct2D off for this run ({why}), back to WPF");
        _renderRun = false;
        _disp?.BeginInvoke(new Action(() => { if (_level > 0) ApplyOnThread(); }));
    }

    // ---------- placement ----------

    /// <summary>Six spots: 0 top left (SteamOS), 1 top centre, 2 top right, 3 bottom left, 4 bottom centre, 5 bottom right,
    /// on the monitor of the foreground window.</summary>
    private void Place()
    {
        IntPtr hwnd;
        int w, h;
        if (_hwnd != IntPtr.Zero)
        {
            if (!_renderShow) return;
            hwnd = _hwnd; w = _pxW; h = _pxH;
            if (w <= 0 || h <= 0) return;
        }
        else if (_win is { IsVisible: true })
        {
            hwnd = new WindowInteropHelper(_win).Handle;
            var src = PresentationSource.FromVisual(_win);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
            w = (int)Math.Ceiling(_win.ActualWidth * sx); h = (int)Math.Ceiling(_win.ActualHeight * sy);
        }
        else return;
        var mon = MonitorFromWindow(GetForegroundWindow(), 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return;
        var r = mi.rcMonitor;
        int x = (_position % 3) switch { 0 => r.Left, 1 => r.Left + (r.Right - r.Left - w) / 2, _ => r.Right - w };
        int y = _position < 3 ? r.Top : r.Bottom - h;
        // the X / Y nudge (% of the screen), clamped so the overlay never leaves the monitor
        x = Math.Clamp(x + (r.Right - r.Left) * _offX / 100, r.Left, Math.Max(r.Left, r.Right - w));
        y = Math.Clamp(y + (r.Bottom - r.Top) * _offY / 100, r.Top, Math.Max(r.Top, r.Bottom - h));
        if (hwnd == _hwnd) SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE);
        else SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ---------- Win32 ----------

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000,
                      WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    private const uint LWA_ALPHA = 0x2;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    private delegate IntPtr WndProcFn(IntPtr h, uint msg, IntPtr w, IntPtr l);
    private static readonly WndProcFn Proc = (h, m, w, l) =>
    {
        const uint WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21;
        if (m == WM_NCHITTEST) return new IntPtr(-1);        // HTTRANSPARENT
        if (m == WM_MOUSEACTIVATE) return new IntPtr(3);     // MA_NOACTIVATE
        return DefWindowProc(h, m, w, l);
    };
    private static bool _classDone;
    private const string ClassName = "AlienControlsOverlayD2D";

    /// <summary>ov.15: a bare popup for DirectComposition: no redirection bitmap (nothing for the CPU to copy),
    /// layered + transparent so clicks go through to the game, tool window (no taskbar / Alt+Tab), never activated.</summary>
    private static IntPtr CreateOverlayHwnd()
    {
        var inst = GetModuleHandle(null);
        if (!_classDone)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = inst, lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);   // 0 when already registered (a second overlay thread): fine
            _classDone = true;
        }
        var h = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE,
            ClassName, "Alien Controls overlay", WS_POPUP, 0, 0, 64, 32, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (h != IntPtr.Zero) SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA);   // a layered window shows nothing until this
        return h;
    }

    private static void MakeClickThrough(IntPtr h)
    {
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
    }

    public void Dispose()
    {
        _level = 0;
        var d = _disp;
        if (d is null || d.HasShutdownStarted) return;
        try
        {
            d.Invoke(() => { _timer?.Stop(); _perf.Stop(); CloseWindows(); }, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
            d.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        catch (Exception e) { Log.Warn("Overlay.Dispose", e); }
    }
}

/// <summary>
/// ov.15: the overlay's own measurement, logged every 10 s while it's shown, so the renderers can be compared on the
/// laptop: frames drawn, refreshes missed (a gap over 1.5x the refresh interval while the graph is moving) and this
/// app's CPU. Example: "Overlay perf: d2d, 144 Hz, 1438 frames, 2 missed, 0.4 % CPU".
/// </summary>
internal sealed class OverlayPerf
{
    private readonly object _l = new();
    private string _renderer = "";
    private int _hz = 60;
    private long _start, _last;
    private int _frames, _missed;
    private TimeSpan _cpu0;
    private bool _on;

    public void Start(string renderer, int hz)
    {
        lock (_l)
        {
            if (_on && renderer == _renderer && hz == _hz) return;
            _renderer = renderer; _hz = Math.Clamp(hz, 24, 500);
            Reset(Stopwatch.GetTimestamp());
            _on = true;
        }
    }

    public void Stop() { lock (_l) _on = false; }

    private void Reset(long now)
    {
        _start = now; _last = 0; _frames = 0; _missed = 0;
        try { using var p = Process.GetCurrentProcess(); _cpu0 = p.TotalProcessorTime; } catch (Exception) { _cpu0 = TimeSpan.Zero; }
    }

    /// <summary>One frame reached the screen (D2D: Present returned; WPF: a Rendering tick). moving = the graph is
    /// shown, so every refresh should have a frame; a still overlay only draws when its numbers change (not counted).</summary>
    public void Frame(bool moving = true)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_l)
        {
            if (!_on) return;
            if (!moving) { _last = 0; return; }
            if (_last != 0 && now - _last > Stopwatch.Frequency * 1.5 / _hz) _missed++;
            _last = now;
            _frames++;
        }
    }

    /// <summary>Called from the number timer: writes the line once 10 s have passed.</summary>
    public void MaybeLog()
    {
        long now = Stopwatch.GetTimestamp();
        string? line = null;
        lock (_l)
        {
            if (!_on || now - _start < Stopwatch.Frequency * 10) return;
            double sec = (now - _start) / (double)Stopwatch.Frequency;
            double cpu = 0;
            try { using var p = Process.GetCurrentProcess(); cpu = (p.TotalProcessorTime - _cpu0).TotalSeconds / sec / Environment.ProcessorCount * 100; }
            catch (Exception) { /* no reading */ }
            line = $"Overlay perf: {_renderer}, {_hz} Hz, {_frames} frames in {sec:0} s, {_missed} missed, {cpu:0.0} % CPU";
            Reset(now);
        }
        Log.Info("Overlay", line);
    }
}
