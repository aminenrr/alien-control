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
        if (Schtasks($"/Query /TN \"{TaskName}\" /XML", out var xml) == 0 && NeedsRebuild(xml, Environment.ProcessPath)) SetEnabled(true);
    }

    /// <summary>2.0.0-beta1.3 (services review #4): exe = the running exe. The task is also rebuilt when its
    /// &lt;Command&gt; points somewhere else (a release zip extracted to a new folder kept launching the old exe).
    /// Null exe skips that check.</summary>
    internal static bool NeedsRebuild(string xml, string? exe = null) =>
        xml.Contains("<DisallowStartIfOnBatteries>true", StringComparison.OrdinalIgnoreCase)
        || xml.Contains("<StopIfGoingOnBatteries>true", StringComparison.OrdinalIgnoreCase)
        || xml.Contains("<ExecutionTimeLimit>PT72H", StringComparison.OrdinalIgnoreCase)
        || !xml.Contains("<DisallowStartIfOnBatteries>", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrEmpty(exe) && !SamePath(TaskCommand(xml), exe));

    /// <summary>The task's &lt;Command&gt;, XML entities decoded and quotes trimmed; null when there is none.</summary>
    internal static string? TaskCommand(string xml)
    {
        var m = System.Text.RegularExpressions.Regex.Match(xml, @"<Command>(.*?)</Command>",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim().Trim('"') : null;
    }

    private static bool SamePath(string? a, string b)
    {
        if (string.IsNullOrEmpty(a)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static bool Exists(string name) => Schtasks($"/Query /TN \"{name}\"", out _) == 0;

    public static bool SetEnabled(bool enable)
    {
        if (!enable) return Schtasks($"/Delete /TN \"{TaskName}\" /F", out _) == 0;
        var exe = Environment.ProcessPath ?? "";
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var file = Path.Combine(Path.GetTempPath(), $"AlienControls-task-{Guid.NewGuid():N}.xml");
        var enc = System.Text.Encoding.Unicode;
        byte[] bytes = [.. enc.GetPreamble(), .. enc.GetBytes(TaskXml(exe, user))];
        try
        {
            // 2.0.0-beta1.3 (services review #4): %TEMP% is writable by any unelevated process of this user, and the
            // task runs elevated at every logon. The file is written, then reopened read-only with FileShare.Read
            // (no write, no delete, no rename) and held open until schtasks exits. Its bytes are read back through that
            // handle first, so a file swapped between the write and the reopen is refused instead of registered.
            using (var w = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { w.Write(bytes); w.Flush(true); }
            using var pin = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var back = new byte[bytes.Length + 1];
            int n = 0, k;
            while (n < back.Length && (k = pin.Read(back, n, back.Length - n)) > 0) n += k;
            if (n != bytes.Length || !back.AsSpan(0, n).SequenceEqual(bytes))
            {
                Log.Info("Startup", "task XML changed on disk before schtasks read it: not registered");
                return false;
            }
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
