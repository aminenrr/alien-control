using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading.Tasks;

namespace AlienControl.Services;

/// <summary>
/// Run-on-startup via a Task Scheduler task with highest privileges, so Windows doesn't
/// show a UAC prompt at every logon (a Run registry key can't start an admin app silently).
/// 1.3.0-beta.1 (review #2): the task is created from XML. A plain schtasks /Create defaults to "start only on AC",
/// "stop on battery" and a 72 h limit, so booting on battery skipped the app and unplugging could kill it.
/// Older tasks with those conditions are rebuilt once.
/// </summary>
public static class Startup
{
    private const string TaskName = "Alien Controls";
    private const string OldTaskName = "AlienControl"; // name used up to 1.1.0

    public static Task<bool> IsEnabledAsync() => Task.Run(IsEnabled);
    public static Task<bool> SetEnabledAsync(bool on) => Task.Run(() => SetEnabled(on));

    public static bool IsEnabled()
    {
        Migrate();
        return Exists(TaskName);
    }

    private static void Migrate()
    {
        if (Exists(OldTaskName))
        {
            if (Exists(TaskName) || SetEnabled(true)) Schtasks($"/Delete /TN \"{OldTaskName}\" /F", out _);
            return;
        }
        // battery conditions from the pre-1.3 task, or a task pointing at another exe path: rebuild it
        if (Schtasks($"/Query /TN \"{TaskName}\" /XML", out var xml) == 0 && NeedsRebuild(xml)) SetEnabled(true);
    }

    internal static bool NeedsRebuild(string xml) =>
        xml.Contains("<DisallowStartIfOnBatteries>true", StringComparison.OrdinalIgnoreCase)
        || xml.Contains("<StopIfGoingOnBatteries>true", StringComparison.OrdinalIgnoreCase)
        || xml.Contains("<ExecutionTimeLimit>PT72H", StringComparison.OrdinalIgnoreCase)
        || !xml.Contains("<DisallowStartIfOnBatteries>", StringComparison.OrdinalIgnoreCase);

    private static bool Exists(string name) => Schtasks($"/Query /TN \"{name}\"", out _) == 0;

    public static bool SetEnabled(bool enable)
    {
        if (!enable) return Schtasks($"/Delete /TN \"{TaskName}\" /F", out _) == 0;
        var exe = Environment.ProcessPath ?? "";
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var file = Path.Combine(Path.GetTempPath(), $"AlienControls-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, TaskXml(exe, user), System.Text.Encoding.Unicode);
            return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", out _) == 0;
        }
        catch (Exception ex) { Log.Warn("Startup.SetEnabled", ex); return false; }
        finally { try { File.Delete(file); } catch (Exception ex) { Log.Warn("Startup temp", ex); } }
    }

    internal static string TaskXml(string exe, string user)
    {
        string X(string v) => SecurityElement.Escape(v) ?? "";
        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Starts Alien Controls in the tray at logon.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{X(user)}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{X(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{X(exe)}</Command><Arguments>--tray</Arguments></Exec></Actions>
</Task>";
    }

    private static int Schtasks(string args, out string output)
    {
        output = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(SysExe.Get("schtasks.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            var read = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("Startup.cs:104", __e); } return -1; }
            output = read.Result;
            return p.ExitCode;
        }
        catch (Exception ex) { Log.Warn("schtasks", ex); return -1; }
    }
}
