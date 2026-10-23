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

    /// <summary>Applies the caps for the given AC / battery boost levels. Returns null or an error.</summary>
    public static async Task<string?> ApplyAsync(int acLevel, int dcLevel)
    {
        int ac = CapFor(acLevel), dc = CapFor(dcLevel);
        foreach (var g in new[] { Max0, Max1 })
        {
            var e = await Run($"/setacvalueindex scheme_current {Sub} {g} {ac}") ?? await Run($"/setdcvalueindex scheme_current {Sub} {g} {dc}");
            if (e is not null && g == Max0) return e;   // PROCFREQMAX1 is missing on non-hybrid CPUs: fine
        }
        return await Run("/setactive scheme_current");
    }

    /// <summary>2.0.0-beta1.3 (services review #2): clears the cap synchronously (no cap on AC or battery), for the
    /// crash handler and Windows ending the session, where nothing async gets to run. Each powercfg call has a 3 s
    /// timeout.</summary>
    public static string? Clear()
    {
        foreach (var g in new[] { Max0, Max1 })
        {
            var e = RunSync($"/setacvalueindex scheme_current {Sub} {g} 0") ?? RunSync($"/setdcvalueindex scheme_current {Sub} {g} 0");
            if (e is not null && g == Max0) return e;   // PROCFREQMAX1 is missing on non-hybrid CPUs: fine
        }
        return RunSync("/setactive scheme_current");
    }

    // 2.0.0-beta1.3 (services review #7): through ProcessRunner (timeout, stdout and stderr read together)
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
