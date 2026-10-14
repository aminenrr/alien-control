using System;
using System.Diagnostics;

namespace AlienControl.Services;

/// <summary>
/// Run-on-startup via a Task Scheduler task with highest privileges, so Windows doesn't
/// show a UAC prompt at every logon (a Run registry key can't start an admin app silently).
/// </summary>
public static class Startup
{
    private const string TaskName = "AlienControl";

    public static bool IsEnabled() => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static bool SetEnabled(bool enable)
    {
        if (!enable) return Schtasks($"/Delete /TN \"{TaskName}\" /F") == 0;
        var exe = Environment.ProcessPath ?? "";
        return Schtasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --tray\" /SC ONLOGON /RL HIGHEST /F") == 0;
    }

    private static int Schtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            p.WaitForExit(5000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
