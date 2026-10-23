using System;
using System.Threading;

namespace AlienControl.Services;

/// <summary>
/// 2.0.0-beta1.3 (services review #1): the one owner of NVML. GpuOc and PowerMeter used to open their own sessions
/// with their own locks, and only the power meter was stopped for RTX off, so an OC write could still hold NVML while
/// pnputil removed the RTX (the 1.2.0-beta.4 native crash class). Now every NVML call runs under one lock with one
/// device handle, and BeginDeviceRemoval waits for the call in flight, closes the session and refuses new calls
/// until EndDeviceRemoval.
/// The session is opened lazily. A call with keepOpen (the power meter, sampled every second while the window shows
/// power) leaves it open until Release; a call without (GpuOc) closes it again unless a kept user has it open, so
/// an OC write alone still leaves nothing loaded, as before.
/// </summary>
internal static class NvmlSession
{
    public enum Fail { Busy, Removing, Init, NoDevice }

    private static readonly object Lock = new();
    private static bool _up, _kept;
    private static int _removing;
    private static IntPtr _dev;

    /// <summary>True while an RTX removal / re-add (pnputil) is running.</summary>
    public static bool Removing { get { lock (Lock) return _removing > 0; } }

    /// <summary>Runs body with the RTX handle under the one NVML lock. failed gets the stage and the NVML code when
    /// the session can't be opened (or the lock isn't free within lockTimeoutMs, or a removal is running).
    /// DllNotFound / EntryPointNotFound propagate to the caller, as before.</summary>
    public static T Run<T>(bool keepOpen, Func<IntPtr, T> body, Func<Fail, int, T> failed, int lockTimeoutMs = Timeout.Infinite)
    {
        if (!Monitor.TryEnter(Lock, lockTimeoutMs)) return failed(Fail.Busy, 0);
        try
        {
            if (_removing > 0) return failed(Fail.Removing, 0);
            if (!_up)
            {
                int r = Nvml.nvmlInit_v2();
                if (r != 0) return failed(Fail.Init, r);
                try
                {
                    r = Nvml.nvmlDeviceGetHandleByIndex_v2(0, out _dev);
                    if (r != 0) { Nvml.nvmlShutdown(); return failed(Fail.NoDevice, r); }
                }
                catch { try { Nvml.nvmlShutdown(); } catch (Exception e) { Log.Warn("NvmlSession.Open", e); } throw; }
                _up = true;
            }
            if (keepOpen) _kept = true;
            try { return body(_dev); }
            finally { if (!_kept) CloseLocked(); }
        }
        finally { Monitor.Exit(Lock); }
    }

    /// <summary>Drops the kept session (power meter disposed, GPU off, or a read failed). Waits for a call in flight.</summary>
    public static void Release()
    {
        lock (Lock) { _kept = false; CloseLocked(); }
    }

    /// <summary>Before pnputil disables (or re-enables) the RTX: waits for the NVML call in flight, closes the session
    /// and makes every call fail with Removing until EndDeviceRemoval. Blocks for that call (milliseconds): call it
    /// off the UI thread.</summary>
    public static void BeginDeviceRemoval()
    {
        lock (Lock) { _removing++; _kept = false; CloseLocked(); }
    }

    public static void EndDeviceRemoval()
    {
        lock (Lock) if (_removing > 0) _removing--;
    }

    private static void CloseLocked()
    {
        if (!_up) return;
        _up = false;
        _dev = IntPtr.Zero;
        try { Nvml.nvmlShutdown(); } catch (Exception e) { Log.Warn("NvmlSession.Close", e); }
    }
}
