using System;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AlienControl.Services;

/// <summary>2.0.3 (Amine): periodic ControlTrace(EVENT_TRACE_CONTROL_FLUSH) on our own PresentMon ETW session.
/// Stock PresentMon 2.6.0 only hands frames over when an ETW buffer fills or the session's 1 s flush timer fires, so
/// at low event rates the overlay saw frames ~1.6 s late. Flushing every 50 ms (measured on diag/overlay-latency)
/// cut the data age to ~0.18 s with no event loss and no measurable CPU. Only our session (by name) is touched,
/// and only while that PresentMon run is alive.</summary>
public static class EtwFlush
{
    public const int PeriodMs = 50;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
    private static extern uint ControlTrace(ulong handle, string name, IntPtr props, uint code);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private const uint EVENT_TRACE_CONTROL_FLUSH = 3;
    private const int PropsSize = 120;                     // sizeof(EVENT_TRACE_PROPERTIES) on x64
    private const int PropsBuf = PropsSize + 2 * 1024 * 2; // room for the logger and log file names

    /// <summary>Starts the flush thread for one PresentMon run; it ends on its own once alive() is false.</summary>
    public static void Start(string session, Func<bool> alive)
    {
        new Thread(() => Loop(session, alive)) { IsBackground = true, Name = "PresentMon ETW flush", Priority = ThreadPriority.AboveNormal }.Start();
    }

    /// <summary>The next flush deadline after one at due issued at now: one period on, or one period from now when the
    /// thread fell behind (re-anchor, never a burst of catch-up flushes).</summary>
    internal static long NextDue(long due, long now, long period) => due + period >= now ? due + period : now + period;

    /// <summary>How long to sleep (ms, 0 to period) until due.</summary>
    internal static int SleepMs(long due, long now, long freq, int periodMs) =>
        (int)Math.Clamp(Math.Ceiling((due - now) * 1000.0 / freq), 0, periodMs);

    private static void Loop(string session, Func<bool> alive)
    {
        IntPtr props = IntPtr.Zero;
        bool period = false;
        uint lastRc = 0;
        bool okLogged = false;
        try
        {
            props = Marshal.AllocHGlobal(PropsBuf);
            period = timeBeginPeriod(1) == 0;   // 50 ms needs better than the default 15.6 ms tick
            long freq = Stopwatch.Frequency, step = freq * PeriodMs / 1000;
            long due = Stopwatch.GetTimestamp() + step;
            while (alive())
            {
                long now = Stopwatch.GetTimestamp();
                if (now < due) { Thread.Sleep(SleepMs(due, now, freq, PeriodMs)); continue; }
                for (int i = 0; i < PropsBuf; i += 4) Marshal.WriteInt32(props, i, 0);
                Marshal.WriteInt32(props, 0, PropsBuf);            // Wnode.BufferSize
                Marshal.WriteInt32(props, 112, PropsSize + 2048);   // LogFileNameOffset
                Marshal.WriteInt32(props, 116, PropsSize);          // LoggerNameOffset
                uint rc = ControlTrace(0, session, props, EVENT_TRACE_CONTROL_FLUSH);
                // 4201 (instance not found) is normal for a moment while PresentMon creates or closes the session
                if (rc != 0 && rc != lastRc && rc != 4201) Log.Info("Frames", $"ETW flush on {session} failed rc {rc}");
                if (rc == 0 && !okLogged) { okLogged = true; Log.Info("Frames", $"ETW flush on {session} every {PeriodMs} ms"); }
                lastRc = rc;
                due = NextDue(due, Stopwatch.GetTimestamp(), step);
            }
        }
        catch (Exception e) { Log.Warn("Frames.EtwFlush", e); }
        finally
        {
            if (props != IntPtr.Zero) Marshal.FreeHGlobal(props);
            if (period) timeEndPeriod(1);
        }
    }
}
