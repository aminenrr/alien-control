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
/// Time based (a slow frame skips ahead, never stretches the slide). The per-frame hook (CompositionTarget.Rendering
/// plus a 15 ms backup timer, in case Rendering stalls) exists only while a slide runs; idle cost is zero.
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
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _backup;
    private int _x, _fromY, _toY, _ms;
    private CubicBezier _ease = CubicBezier.Decelerate;
    private Action? _done;
    private bool _hooked;

    public WindowSlide(Func<IntPtr> hwnd)
    {
        _hwnd = hwnd;
        _backup = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _backup.Tick += (_, _) => Step();
    }

    public bool Active { get; private set; }

    /// <summary>Slides from the window's CURRENT top to <paramref name="toY"/> (device pixels) at column x.
    /// A slide already running is replaced (its completion callback is dropped), so a reversal starts where it is.</summary>
    public void Run(int x, int toY, int ms, CubicBezier ease, Action? done)
    {
        Stop();
        var h = _hwnd();
        if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) { done?.Invoke(); return; }
        _x = x; _fromY = r.Top; _toY = toY; _ms = Math.Max(1, ms); _ease = ease; _done = done;
        Active = true;
        _clock.Restart();
        CompositionTarget.Rendering += OnRendering;
        _hooked = true;
        _backup.Start();
        Step();
    }

    /// <summary>Stops a running slide where it is, without its completion callback.</summary>
    public void Stop()
    {
        if (_hooked) { CompositionTarget.Rendering -= OnRendering; _hooked = false; }
        _backup.Stop();
        _clock.Reset();
        Active = false;
        _done = null;
    }

    private void OnRendering(object? sender, EventArgs e) => Step();

    private void Step()
    {
        if (!Active) return;
        double t = Math.Min(1.0, _clock.Elapsed.TotalMilliseconds / _ms);
        int y = (int)Math.Round(_fromY + (_toY - _fromY) * _ease.Solve(t));
        Move(_hwnd(), _x, y);
        if (t < 1.0) return;
        var done = _done;
        Stop();
        done?.Invoke();
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
