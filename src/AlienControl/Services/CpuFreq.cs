using System.Diagnostics;
using System.Threading.Tasks;

namespace AlienControl.Services;

/// <summary>
/// v0.9.4 (Amine): Efficient boost is capped at 3.4 GHz. Efficient alone still turbos to ~4 GHz on short bursts, which only
/// adds heat and fan noise in Quiet. Windows "Maximum processor frequency" (PROCFREQMAX, MHz, 0 = no cap), set for both
/// core classes (PROCFREQMAX1 = P-cores on hybrid CPUs) on the active power plan. Every other boost level clears the cap.
/// </summary>
public static class CpuFreq
{
    public const int EfficientCapMhz = 3400;
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

    private static async Task<string?> Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg.exe", args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            string o = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? null : "powercfg: " + o.Trim();
        }
        catch (System.Exception ex) { return "powercfg: " + ex.Message; }
    }
}
