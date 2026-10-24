using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AlienControl.Services;

/// <summary>
/// beta1.3-ov (ov.7, Amine): the display's real refresh rate, like RTSS shows it. With G-Sync / VRR the panel doesn't
/// run at the mode's 240 Hz: it follows the game (and the driver repeats frames below the VRR range), so the mode
/// from EnumDisplaySettings is the wrong number. This counts real vblanks of the monitor the game is on with
/// D3DKMTWaitForVerticalBlankEvent on one background thread that sleeps in the kernel between vblanks.
/// Runs only while the overlay shows refresh (levels 2 and 4); Stop ends it.
/// </summary>
public sealed class VblankCounter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct OPENADAPTERFROMHDC { public IntPtr hDc; public uint hAdapter; public LUID Luid; public uint VidPnSourceId; }
    [StructLayout(LayoutKind.Sequential)] private struct WAITFORVBLANK { public uint hAdapter; public uint hDevice; public uint VidPnSourceId; }
    [StructLayout(LayoutKind.Sequential)] private struct CLOSEADAPTER { public uint hAdapter; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromHdc(ref OPENADAPTERFROMHDC p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTWaitForVerticalBlankEvent(ref WAITFORVBLANK p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER p);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr init);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFOEX info);

    private readonly long[] _ring = new long[512];
    private int _head, _count;
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _run;
    private volatile int _gen;   // a Stop + Start while the old thread still waits on a vblank: the old one sees a new gen and ends

    public bool Running => _run;

    /// <summary>The display mode's Hz (ov.7.1). The panel can't refresh faster than this, so vblank events closer than
    /// 60 % of its period are duplicates (the wait can return twice per refresh, seen as 428 Hz on a 240 Hz panel) and
    /// are dropped, and the count never goes above it. 0 = unknown.</summary>
    public int MaxHz { get; set; }

    public void Start()
    {
        if (_run) return;
        _run = true;
        int gen = ++_gen;
        lock (_lock) { _head = 0; _count = 0; }
        _thread = new Thread(() => Loop(gen)) { IsBackground = true, Name = "Overlay vblank", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Ends the thread after its current wait (at most one refresh).</summary>
    public void Stop() => _run = false;

    public void Dispose() => Stop();

    /// <summary>Vblanks in the last second, null until a full second was seen (or the counter isn't working).</summary>
    public int? Hz
    {
        get
        {
            long now = Stopwatch.GetTimestamp(), from = now - Stopwatch.Frequency;
            lock (_lock)
            {
                if (_count < 2) return null;
                long newest = _ring[(_head - 1 + _ring.Length) % _ring.Length];
                if (now - newest > Stopwatch.Frequency / 2) return null;   // stalled: let the caller fall back
                long oldest = _ring[(_head - _count + _ring.Length) % _ring.Length];
                if (oldest > from) return null;                            // under a second of data yet
                int n = 0;
                for (int i = 1; i <= _count; i++)
                {
                    if (_ring[(_head - i + _ring.Length) % _ring.Length] < from) break;
                    n++;
                }
                int max = MaxHz;
                return max > 0 ? Math.Min(n, max) : n;
            }
        }
    }

    private void Loop(int gen)
    {
        uint adapter = 0, source = 0;
        string device = "";
        long lastCheck = 0;
        int fails = 0;
        try
        {
            while (_run && gen == _gen)
            {
                long now = Stopwatch.GetTimestamp();
                if (adapter == 0 || now - lastCheck > Stopwatch.Frequency * 2)
                {
                    lastCheck = now;
                    string dev = GameMonitor();
                    if (adapter == 0 || dev != device)
                    {
                        Close(ref adapter);
                        device = dev;
                        if (!Open(dev, out adapter, out source)) { if (++fails > 5) { Log.Info("Vblank", "can't open the display, falling back to the mode's Hz"); return; } Thread.Sleep(500); continue; }
                        lock (_lock) { _count = 0; }
                    }
                }
                var w = new WAITFORVBLANK { hAdapter = adapter, VidPnSourceId = source };
                if (D3DKMTWaitForVerticalBlankEvent(ref w) != 0)
                {
                    Close(ref adapter);   // mode change, GPU switch, sleep: reopen
                    if (++fails > 20) { Log.Info("Vblank", "vblank wait keeps failing, falling back to the mode's Hz"); return; }
                    Thread.Sleep(250);
                    continue;
                }
                fails = 0;
                if (gen != _gen) break;
                long t = Stopwatch.GetTimestamp();
                int max = MaxHz;
                long minGap = Stopwatch.Frequency * 6 / (10 * (max > 0 ? max : 1000));
                lock (_lock)
                {
                    if (_count > 0 && t - _ring[(_head - 1 + _ring.Length) % _ring.Length] < minGap) continue;
                    _ring[_head] = t;
                    _head = (_head + 1) % _ring.Length;
                    if (_count < _ring.Length) _count++;
                }
            }
        }
        catch (Exception ex) { Log.Warn("Vblank", ex); }
        finally { Close(ref adapter); if (gen == _gen) _run = false; }
    }

    private static string GameMonitor()
    {
        var mon = MonitorFromWindow(GetForegroundWindow(), 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi) ? mi.szDevice : "";
    }

    private static bool Open(string device, out uint adapter, out uint source)
    {
        adapter = 0; source = 0;
        if (device.Length == 0) return false;
        var hdc = CreateDC("DISPLAY", device, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            var o = new OPENADAPTERFROMHDC { hDc = hdc };
            if (D3DKMTOpenAdapterFromHdc(ref o) != 0) return false;
            adapter = o.hAdapter; source = o.VidPnSourceId;
            return true;
        }
        finally { DeleteDC(hdc); }
    }

    private static void Close(ref uint adapter)
    {
        if (adapter == 0) return;
        var c = new CLOSEADAPTER { hAdapter = adapter };
        D3DKMTCloseAdapter(ref c);
        adapter = 0;
    }
}
