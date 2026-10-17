using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace AlienControl.Services;

/// <summary>
/// v0.8.8: slides a whole top-level window (HWND) vertically with native SetWindowPos, one step per frame.
/// Why: v0.8.6/0.8.7 animated the card INSIDE the transparent (layered) WPF window, and the laptop's screen
/// recording showed zero in-between frames, open and close. Moving the HWND is done by the compositor (DWM)
/// on the already-drawn window, so WPF never has to present a new frame for the motion to show.
/// Time based (a slow frame skips ahead, never stretches the slide). 1.3.0: a short-lived thread steps it once per
/// composition (see RunThreaded); it exists only while a slide runs, so idle cost is zero.
/// </summary>
public sealed class WindowSlide
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private readonly Func<IntPtr> _hwnd;
    private int _x, _fromY, _toY, _ms;
    private CubicBezier _ease = CubicBezier.Decelerate;
    private Action? _done;

    public WindowSlide(Func<IntPtr> hwnd)
    {
        _hwnd = hwnd;
    }

    public bool Active { get; private set; }

    // ---------- 1.3.0-beta.1 slide engine (Part 3) ----------
    // A short-lived thread steps the slide once per composition (DwmFlush), placing each frame for the vblank it'll
    // show on, with async SetWindowPos (240 steps/s on the 240 Hz panel, never stalled by the UI thread). The 1.2
    // UI-thread stepper was removed in 1.3.0 after the beta A/B (the user: the new engine feels better).
    /// <summary>Debug: log steps/s and the biggest gap of every slide to crash.log.</summary>
    public static bool LogFrames { get; set; }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetCompositionTimingInfo(IntPtr h, ref DWM_TIMING_INFO info);
    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceCounter(out long c);
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]   // dwmapi.h packs this struct (pshpack1)
    private struct DWM_TIMING_INFO
    {
        public uint cbSize; public uint rateRefreshNum, rateRefreshDen; public ulong qpcRefreshPeriod;
        public uint rateComposeNum, rateComposeDen; public ulong qpcVBlank, cRefresh; public uint cDXRefresh;
        public ulong qpcCompose, cFrame; public uint cDXPresent; public ulong cRefreshFrame, cFrameSubmitted;
        public uint cDXPresentSubmitted; public ulong cFrameConfirmed; public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed; public uint cDXRefreshConfirmed; public ulong cFramesLate;
        public uint cFramesOutstanding; public ulong cFrameDisplayed, qpcFrameDisplayed, cRefreshFrameDisplayed,
            cFrameComplete, qpcFrameComplete, cFramePending, qpcFramePending, cFramesDisplayed, cFramesComplete,
            cFramesPending, cFramesAvailable, cFramesDropped, cFramesMissed, cRefreshNextDisplayed,
            cRefreshNextPresented, cRefreshesDisplayed, cRefreshesPresented, cRefreshStarted, cPixelsReceived,
            cPixelsDrawn, cBuffersEmpty;
    }

    private int _gen;
    private Dispatcher? _ui;

    private void RunThreaded(IntPtr h)
    {
        int gen = ++_gen;
        _ui = Dispatcher.CurrentDispatcher;
        int x = _x, fromY = _fromY, toY = _toY, ms = _ms;
        var ease = _ease;
        bool log = LogFrames;
        NoGcStart();
        var t = new System.Threading.Thread(() =>
        {
            long freq = Stopwatch.Frequency;
            // 1.3.0: wait for one composition before the clock starts. A window or popup that was just shown can block
            // the first DwmFlush for 60-100 ms (its first frame); starting the clock before it made the slide jump.
            DwmFlush();
            var sw = Stopwatch.StartNew();
            double last = 0, maxGap = 0; int steps = 0;
            var ti = new DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>() };
            while (gen == System.Threading.Volatile.Read(ref _gen))
            {
                if (DwmFlush() != 0) System.Threading.Thread.Sleep(4);   // DWM busy: fall back to ~240 Hz sleeps
                double now = sw.Elapsed.TotalMilliseconds;
                // place the frame for the vblank it will be shown on, not for when this code runs
                double ahead = 0;
                if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref ti) == 0 && ti.qpcRefreshPeriod > 0 && QueryPerformanceCounter(out long qpc))
                {
                    double period = ti.qpcRefreshPeriod * 1000.0 / freq;
                    double sinceVb = (qpc - (long)ti.qpcVBlank) * 1000.0 / freq;
                    double toNext = period - (sinceVb % period + period) % period;
                    ahead = Math.Min(toNext + period, 2 * period);
                }
                double p = Math.Min(1.0, (now + ahead) / ms);
                if (now >= ms) p = 1.0;
                int y = (int)Math.Round(fromY + (toY - fromY) * ease.Solve(p));
                if (gen != System.Threading.Volatile.Read(ref _gen)) break;
                SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS);
                steps++; maxGap = Math.Max(maxGap, now - last); last = now;
                if (p >= 1.0)
                {
                    if (log) Log.Info("Slide", $"thread {ms} ms: {steps} steps, {steps * 1000.0 / Math.Max(1, now):0}/s, biggest gap {maxGap:0.0} ms");
                    _ui?.BeginInvoke(new Action(() =>
                    {
                        if (gen != _gen) return;
                        var done = _done;
                        Active = false; _done = null; NoGcEnd();
                        done?.Invoke();
                    }));
                    return;
                }
            }
        }) { IsBackground = true, Name = "AlienControl slide", Priority = System.Threading.ThreadPriority.AboveNormal };
        t.Start();
    }

    // A non-concurrent GC can freeze the UI thread for a frame or two mid-slide: hold GCs for the slide's length.
    private static bool _noGc;
    private static void NoGcStart()
    {
        if (_noGc) return;
        try { _noGc = GC.TryStartNoGCRegion(16 * 1024 * 1024); } catch (Exception ex) { Log.Warn("NoGcStart", ex); }
    }
    private static void NoGcEnd()
    {
        if (!_noGc) return;
        _noGc = false;
        try { if (System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion) GC.EndNoGCRegion(); }
        catch (Exception ex) { Log.Warn("NoGcEnd", ex); }
    }

    /// <summary>Slides from the window's CURRENT top to <paramref name="toY"/> (device pixels) at column x.
    /// A slide already running is replaced (its completion callback is dropped), so a reversal starts where it is.</summary>
    public void Run(int x, int toY, int ms, CubicBezier ease, Action? done)
    {
        Stop();
        var h = _hwnd();
        if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) { done?.Invoke(); return; }
        _x = x; _fromY = r.Top; _toY = toY; _ms = Math.Max(1, ms); _ease = ease; _done = done;
        Active = true;
        RunThreaded(h);
    }

    /// <summary>Stops a running slide where it is, without its completion callback.</summary>
    public void Stop()
    {
        System.Threading.Interlocked.Increment(ref _gen);   // ends a slide thread
        NoGcEnd();
        Active = false;
        _done = null;
    }

    public static void Move(IntPtr h, int x, int y)
    {
        if (h != IntPtr.Zero) SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public static RECT Rect(IntPtr h) { GetWindowRect(h, out var r); return r; }

    /// <summary>Bottom edge (device pixels) of the monitor the window is on.</summary>
    public static int MonitorBottom(IntPtr h)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var m = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
        return m != IntPtr.Zero && GetMonitorInfo(m, ref mi) ? mi.rcMonitor.Bottom : 100000;
    }
}

/// <summary>CSS-style cubic-bezier(x1, y1, x2, y2) timing curve.</summary>
public readonly struct CubicBezier
{
    private readonly double _x1, _y1, _x2, _y2;
    public CubicBezier(double x1, double y1, double x2, double y2) { _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2; }

    /// <summary>Fluent decelerate (fast start, long soft stop): sliding in.</summary>
    public static readonly CubicBezier Decelerate = new(0, 0, 0, 1);
    /// <summary>1.3.0-beta.1 open curve: softer start (the 1.2 decelerate jumped a big chunk in the first frame).</summary>
    public static readonly CubicBezier Open = new(0.25, 1, 0.5, 1);
    /// <summary>Accelerate (slow start, quick exit): sliding out.</summary>
    public static readonly CubicBezier Accelerate = new(0.7, 0, 1, 0.5);

    private static double B(double a, double b, double t) => 3 * a * t * (1 - t) * (1 - t) + 3 * b * t * t * (1 - t) + t * t * t;

    public double Solve(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double lo = 0, hi = 1, t = x;
        for (int i = 0; i < 30; i++)   // bisection: x(t) is monotonic for x1, x2 in [0, 1]
        {
            t = (lo + hi) / 2;
            if (B(_x1, _x2, t) < x) lo = t; else hi = t;
        }
        return B(_y1, _y2, t);
    }
}
