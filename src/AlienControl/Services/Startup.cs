using System;
using System.Diagnostics;

namespace AlienControl.Services;

/// <summary>
/// Run-on-startup via a Task Scheduler task with highest privileges, so Windows doesn't
/// show a UAC prompt at every logon (a Run registry key can't start an admin app silently).
/// </summary>
public static class Startup
{
    private const string TaskName = "Alien Controls";
    private const string OldTaskName = "AlienControl"; // name used up to 1.1.0

    public static bool IsEnabled()
    {
        Migrate();
        return Exists(TaskName);
    }

    /// <summary>Up to 1.1.0 the task was "AlienControl": swap it for "Alien Controls" (same settings).</summary>
    private static void Migrate()
    {
        if (!Exists(OldTaskName)) return;
        if (Exists(TaskName) || SetEnabled(true)) Schtasks($"/Delete /TN \"{OldTaskName}\" /F");
    }

    private static bool Exists(string name) => Schtasks($"/Query /TN \"{name}\"") == 0;

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
            using var p = Process.Start(new ProcessStartInfo(SysExe.Get("schtasks.exe"), args)
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
