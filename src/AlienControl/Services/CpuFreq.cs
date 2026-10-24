using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AlienControl.Services;

/// <summary>
/// v0.9.4 (Amine): Efficient boost is capped (3.0 GHz since v0.9.5). Efficient alone still turbos to ~4 GHz on short bursts, which only
/// adds heat and fan noise in Quiet. Windows "Maximum processor frequency" (PROCFREQMAX, MHz, 0 = no cap), set for both
/// core classes (PROCFREQMAX1 = P-cores on hybrid CPUs) on the active power plan. Every other boost level clears the cap.
/// </summary>
public static class CpuFreq
{
    public const int EfficientCapMhz = 3000;   // v0.9.5: 3.4 -> 3.0 GHz (day to day: Discord, Vivaldi, Steam)
    private const string Sub = "54533251-82be-4824-96c1-47b60b740d00";        // processor power management
    private const string Max0 = "75b0ae3f-bce0-45a7-8c89-c9611c25e100";       // PROCFREQMAX
    private const string Max1 = "75b0ae3f-bce0-45a7-8c89-c9611c25e101";       // PROCFREQMAX1

    public static int CapFor(int boostLevel) => boostLevel == 3 ? EfficientCapMhz : 0;

    // 2.0.0-beta1.4 (services review #8): in-process powrprof instead of 5 powercfg.exe runs per preset change (2 values
    // x 2 GUIDs + setactive). Microseconds, synchronous, so it also works from the crash and session-end paths. If a
    // call fails (anything other than PROCFREQMAX1 missing on a non-hybrid CPU), the old powercfg path runs instead.
    private static readonly Guid SubG = new(Sub), Max0G = new(Max0), Max1G = new(Max1);
    private const uint ERROR_FILE_NOT_FOUND = 2;

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>Writes both caps on the active scheme and re-activates it. Null = done, else the failing call.
    /// Values that already match are not written (a preset change that keeps the cap costs two reads).</summary>
    internal static string? ApplyNative(int ac, int dc)
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return "PowerGetActiveScheme failed";
        Guid scheme;
        try { scheme = Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
        bool changed = false;
        foreach (var g in new[] { Max0G, Max1G })
        {
            var sub = SubG; var set = g;
            uint rAc = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out uint curAc);
            if (rAc == ERROR_FILE_NOT_FOUND && g == Max1G) continue;   // PROCFREQMAX1 is missing on non-hybrid CPUs: fine
            uint rDc = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out uint curDc);
            if (rAc != 0 || curAc != (uint)ac)
            {
                uint e = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, (uint)ac);
                if (e == ERROR_FILE_NOT_FOUND && g == Max1G) continue;
                if (e != 0) return $"PowerWriteACValueIndex error {e}";
                changed = true;
            }
            if (rDc != 0 || curDc != (uint)dc)
            {
                uint e = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, (uint)dc);
                if (e == ERROR_FILE_NOT_FOUND && g == Max1G) continue;
                if (e != 0) return $"PowerWriteDCValueIndex error {e}";
                changed = true;
            }
        }
        if (!changed) return null;
        uint a = PowerSetActiveScheme(IntPtr.Zero, ref scheme);   // like powercfg /setactive: the new values take effect now
        return a == 0 ? null : $"PowerSetActiveScheme error {a}";
    }

    private static string? TryNative(int ac, int dc)
    {
        try { return ApplyNative(ac, dc); }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Applies the caps for the given AC / battery boost levels. Returns null or an error.</summary>
    public static async Task<string?> ApplyAsync(int acLevel, int dcLevel)
    {
        int ac = CapFor(acLevel), dc = CapFor(dcLevel);
        if (TryNative(ac, dc) is not { } nerr) return null;
        Log.Info("CpuFreq", $"powrprof: {nerr}, falling back to powercfg");
        foreach (var g in new[] { Max0, Max1 })
        {
            var e = await Run($"/setacvalueindex scheme_current {Sub} {g} {ac}") ?? await Run($"/setdcvalueindex scheme_current {Sub} {g} {dc}");
            if (e is not null && g == Max0) return e;   // PROCFREQMAX1 is missing on non-hybrid CPUs: fine
        }
        return await Run("/setactive scheme_current");
    }

    /// <summary>2.0.0-beta1.3 (services review #2): clears the cap synchronously (no cap on AC or battery), for the
    /// crash handler and Windows ending the session, where nothing async gets to run. 2.0.0-beta1.4: powrprof first;
    /// the powercfg fallback has a 3 s timeout per call.</summary>
    public static string? Clear()
    {
        if (TryNative(0, 0) is not { } nerr) return null;
        Log.Info("CpuFreq", $"powrprof: {nerr}, falling back to powercfg");
        foreach (var g in new[] { Max0, Max1 })
        {
            var e = RunSync($"/setacvalueindex scheme_current {Sub} {g} 0") ?? RunSync($"/setdcvalueindex scheme_current {Sub} {g} 0");
            if (e is not null && g == Max0) return e;   // PROCFREQMAX1 is missing on non-hybrid CPUs: fine
        }
        return RunSync("/setactive scheme_current");
    }

    // 2.0.0-beta1.3 (services review #7): the powercfg fallback runs through ProcessRunner (timeout, stdout and stderr
    // read together)
    private const int TimeoutMs = 10000;

    private static async Task<string?> Run(string args)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(SysExe.Get("powercfg.exe"), args, TimeoutMs, kill: true);
            return r.Started && !r.TimedOut && r.ExitCode == 0 ? null : "powercfg: " + r.Output;
        }
        catch (System.Exception ex) { return "powercfg: " + ex.Message; }
    }

    private static string? RunSync(string args)
    {
        try
        {
            var r = ProcessRunner.Run(SysExe.Get("powercfg.exe"), args, 3000);
            return r.Started && !r.TimedOut && r.ExitCode == 0 ? null : "powercfg: " + r.Output;
        }
        catch (System.Exception ex) { return "powercfg: " + ex.Message; }
    }
}
