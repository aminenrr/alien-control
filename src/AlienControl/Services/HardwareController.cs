using System;
using System.Threading;
using System.Threading.Tasks;

namespace AlienControl.Services;

/// <summary>
/// 1.3.0-beta.1 (review #13): one queue for every ACPI-level command: both alienx CLIs, the AWCC WMI sensor reads
/// and the Dell BIOS WMI calls. Two of these at once is what stalled the whole PC (ACPI is serialized in the
/// kernel), so they now run strictly one after another, in order, from any thread. The keyboard HID has its own
/// lock in KeyboardHid (it's USB, not ACPI). Waiting callers cost nothing (async semaphore, no thread).
/// </summary>
public static class HardwareController
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // 2.0.2 (stability audit 4a, Amine): a CLI that timed out and didn't exit after its kill keeps the queue closed
    // until it really exits (it can still be inside an ACPI call). _hold is set by the work running inside the gate
    // (only one runs at a time, so a plain field is safe) and read by the finally that would release the gate.
    private static Task? _hold;
    private static Task _stuck = Task.CompletedTask;

    /// <summary>2.0.2: true while a timed-out CLI is still running and holds the queue. New CLI runs and the WMI
    /// sensor reads fail fast instead of queueing behind it.</summary>
    public static bool Stuck => !_stuck.IsCompleted;

    /// <summary>2.0.2: called from inside the gate: keep the gate closed until exited completes.</summary>
    internal static void HoldUntil(Task exited) => _hold = exited;

    private static void ReleaseGate()
    {
        var hold = _hold;
        _hold = null;
        if (hold is null || hold.IsCompleted) { Gate.Release(); return; }
        _stuck = hold;
        Log.Info("HardwareController", "a timed-out CLI is still running: hardware queue held until it exits");
        _ = hold.ContinueWith(_ =>
        {
            Log.Info("HardwareController", "the timed-out CLI exited: hardware queue open again");
            Gate.Release();
        }, TaskScheduler.Default);
    }

    public static async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try { return await work().ConfigureAwait(false); }
        finally { ReleaseGate(); }
    }

    /// <summary>Blocking variant for code that already runs on a worker thread (WMI reads).</summary>
    public static T Run<T>(Func<T> work)
    {
        Gate.Wait();
        try { return work(); }
        finally { ReleaseGate(); }
    }
}
