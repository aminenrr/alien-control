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

    public static async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try { return await work().ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    /// <summary>Blocking variant for code that already runs on a worker thread (WMI reads).</summary>
    public static T Run<T>(Func<T> work)
    {
        Gate.Wait();
        try { return work(); }
        finally { Gate.Release(); }
    }
}
