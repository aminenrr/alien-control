using System;
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
/// The host pulls frames from FramesSource and the newest Sensors snapshot on its own timer (the update interval).
/// </summary>
public sealed class OverlayHost : IDisposable
{
    private Thread? _thread;
    private Dispatcher? _disp;
    private Window? _win;
    private OverlayPanel? _panel;
    private DispatcherTimer? _timer;
    private readonly FramesSource _frames;
    private volatile Sensors _sensors = new();
    private readonly ManualResetEventSlim _ready = new();

    // settings, written from the app thread, read on the overlay thread
    private volatile int _level, _position, _updateMs = 500, _offX, _offY;
    private double _scale = 0.8, _bg = 0.5;
    private volatile bool _extended, _smooth;
    private Item _hidden;   // ov.9: Show on overlay toggles
    private DispatcherTimer? _graphTimer;

    private readonly FramesSource.Frame[] _buf = new FramesSource.Frame[2048];
    private float[] _graph = new float[240];
    private readonly FramesSource.Playhead _play = new();

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
    /// (ov.11, gives the WPF render memory back). The next level starts them again.</summary>
    public void Apply(int level, int position, double scale, double bg, int updateMs, int offsetX = 0, int offsetY = 0, bool extended = false, bool smooth = false, Item hidden = Item.None)
    {
        _extended = extended;
        _hidden = hidden;
        _smooth = smooth;
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
            _graphTimer?.Stop();
            _win?.Close();
            _win = null;
            _panel = null;
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
            _panel = new OverlayPanel();
            _win = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, Focusable = false, ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight, Content = _panel, Title = "Alien Controls overlay",
            };
            _win.SourceInitialized += (_, _) => MakeClickThrough(new WindowInteropHelper(_win).Handle);
            _win.SizeChanged += (_, _) => Place();
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(_updateMs) };
            _timer.Tick += (_, _) => Tick();
            // ov.7: LIVE mode, the graph alone at ~30 Hz between the number updates
            _graphTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _graphTimer.Tick += (_, _) => GraphTick();
            _idle = new DispatcherTimer(DispatcherPriority.Background) { Interval = IdleTeardown };
            _idle.Tick += (_, _) => TearDown();
            _ready.Set();
            Dispatcher.Run();
            if (_level <= 0) TornDown?.Invoke();
        }
        catch (Exception ex) { Log.Warn("Overlay.Run", ex); _ready.Set(); }
    }

    private void ApplyOnThread()
    {
        if (_win is null || _panel is null || _timer is null) return;
        int level = _level;
        if (level <= 0)
        {
            _timer.Stop();
            _graphTimer?.Stop();
            if (_win.IsVisible) _win.Hide();
            if (_idle is { IsEnabled: false }) { _idle.Interval = IdleTeardown; _idle.Start(); }
            return;
        }
        _idle?.Stop();
        _panel.Level = level;
        _panel.Items = OverlayLevels.Items(level, _extended, _hidden);
        _panel.Scale = _scale;
        _panel.BgAlpha = _bg;
        _timer.Interval = TimeSpan.FromMilliseconds(_updateMs);
        Tick();
        if (!_win.IsVisible) _win.Show();
        Place();
        if (!_timer.IsEnabled) _timer.Start();
        // ov.14: the graph always scrolls at ~30 Hz when it's shown (was LIVE only); numbers keep the chosen period
        bool graph = (_panel.Items & Item.FrameGraph) != 0;
        if (graph) { if (_graphTimer is { IsEnabled: false }) _graphTimer.Start(); }
        else _graphTimer?.Stop();
    }

    /// <summary>LIVE mode: newest frametimes into the graph and a redraw, no new numbers and no re-measure.</summary>
    private void GraphTick()
    {
        if (_panel is null) return;
        int n = _frames.CopyRecent(_buf);
        n = _play.Visible(new ReadOnlySpan<FramesSource.Frame>(_buf, 0, n), System.Diagnostics.Stopwatch.GetTimestamp());
        int g = Math.Min(_graph.Length, n);
        for (int i = 0; i < g; i++) _graph[i] = _buf[n - g + i].Ms;
        _panel.GraphCount = g;
        _panel.InvalidateVisual();
    }

    private int _topTick;

    private void Tick()
    {
        if (_panel is null || _win is null) return;
        Fill(_panel, _frames, _buf, ref _graph, _updateMs);
        if (_graphTimer is { IsEnabled: true }) GraphTick();   // keep the played-back graph, not the newest burst
        _panel.Data = _sensors;
        _panel.InvalidateMeasure();
        _panel.InvalidateVisual();
        // games raise themselves on focus; keep the overlay above them (cheap, no activation)
        if (++_topTick % Math.Max(1, 1000 / _updateMs) == 0) KeepTopmost();
    }

    /// <summary>Frame stats into a panel (shared with the Overlay tab preview). fps = frames over the update period,
    /// like MangoHud's fps_sampling_period; the graph is the newest frametimes.</summary>
    internal static void Fill(OverlayPanel p, FramesSource frames, FramesSource.Frame[] buf, ref float[] graph, int updateMs)
    {
        int n = frames.CopyRecent(buf);
        var span = new ReadOnlySpan<FramesSource.Frame>(buf, 0, n);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        // ov.14: fps over the last 250 ms whatever the update period, so the number follows the game quicker
        long window = System.Diagnostics.Stopwatch.Frequency / 4;
        var st = FramesSource.StatsOver(span, now, window);
        p.Fps = st.Fps;                       // real fps: the game's own frames (= all frames without frame gen)
        p.GenFps = st.GenFps;
        p.FrameMs = st.AvgMs;
        p.LatMs = st.LatMs;
        p.LatPc = st.PcLat;
        int g = Math.Min(graph.Length, n);
        for (int i = 0; i < g; i++) graph[i] = span[n - g + i].Ms;
        p.Graph = graph;
        p.GraphCount = g;
        // lows over every buffered frame (up to 2048, about 8 s at 240 fps), max over the graph like the graph's label
        var (low1, low01, _) = Lows(span);
        p.Low1 = low1;
        p.Low01 = low01;
        p.MaxMs = g > 0 ? Lows(span[(n - g)..]).MaxMs : 0;
        p.Engine = EngineLabel(frames);
        p.PresentMode = frames.PresentMode;
    }

    [ThreadStatic] private static float[]? _lowScratch;

    /// <summary>1 % and 0.1 % low fps (average of the slowest 1 % / 0.1 % of frames, at least one frame) and the max
    /// frametime. One sort of at most 2048 floats per update, no allocation after the first call.</summary>
    internal static (double Low1, double Low01, double MaxMs) Lows(ReadOnlySpan<FramesSource.Frame> f)
    {
        if (f.Length == 0) return (0, 0, 0);
        var buf = _lowScratch is { } b && b.Length >= f.Length ? b : (_lowScratch = new float[Math.Max(f.Length, 2048)]);
        var ms = buf.AsSpan(0, f.Length);
        for (int i = 0; i < f.Length; i++) ms[i] = f[i].Ms;
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
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            bool d3d12 = false, d3d11 = false, vk = false, gl = false;
            foreach (System.Diagnostics.ProcessModule m in p.Modules)
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

    // ---------- placement ----------

    /// <summary>Six spots: 0 top left (SteamOS), 1 top centre, 2 top right, 3 bottom left, 4 bottom centre, 5 bottom right,
    /// on the monitor of the foreground window.</summary>
    private void Place()
    {
        if (_win is null || !_win.IsVisible) return;
        var hwnd = new WindowInteropHelper(_win).Handle;
        var mon = MonitorFromWindow(GetForegroundWindow(), 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return;
        var src = PresentationSource.FromVisual(_win);
        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        int w = (int)Math.Ceiling(_win.ActualWidth * sx), h = (int)Math.Ceiling(_win.ActualHeight * sy);
        var r = mi.rcMonitor;
        int x = (_position % 3) switch { 0 => r.Left, 1 => r.Left + (r.Right - r.Left - w) / 2, _ => r.Right - w };
        int y = _position < 3 ? r.Top : r.Bottom - h;
        // the X / Y nudge (% of the screen), clamped so the overlay never leaves the monitor
        x = Math.Clamp(x + (r.Right - r.Left) * _offX / 100, r.Left, Math.Max(r.Left, r.Right - w));
        y = Math.Clamp(y + (r.Bottom - r.Top) * _offY / 100, r.Top, Math.Max(r.Top, r.Bottom - h));
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void KeepTopmost()
    {
        if (_win is null) return;
        Place();
    }

    // ---------- Win32 ----------

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

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
            d.Invoke(() => { _timer?.Stop(); _win?.Close(); }, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(1));
            d.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        catch (Exception e) { Log.Warn("Overlay.Dispose", e); }
    }
}
