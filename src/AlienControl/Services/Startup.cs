using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;

namespace AlienControl.Services;

/// <summary>
/// Run-on-startup via a Task Scheduler task with highest privileges, so Windows doesn't
/// show a UAC prompt at every logon (a Run registry key can't start an admin app silently).
/// 1.3.0-beta.1 (review #2): the task is created from XML. A plain schtasks /Create defaults to "start only on AC",
/// "stop on battery" and a 72 h limit, so booting on battery skipped the app and unplugging could kill it.
/// Older tasks with those conditions are rebuilt once.
///
/// 2.0.0-beta1.4 (startup task security): the task starts the app as admin at every logon, so the exe it launches and
/// every file Windows loads next to it must be changeable by admins only. A zip extracted to Downloads or the Desktop
/// is writable by any unelevated program of that user (swap the exe, or drop a version.dll next to it).
/// <list type="bullet">
/// <item>Turning it on copies the app (exe, tools\ with the alienfx-tools CLIs, LICENSE, README; a dev build's DLLs) to %ProgramFiles%\AlienControl with an
/// admin-only ACL and points the task there. The task is only ever registered or repointed to a path that passes
/// <see cref="IsAdminOnly"/>.</item>
/// <item>A newer version run from elsewhere updates that copy (staged as *.new, then swapped; a file in use is renamed
/// to *.old first, and anything left is finished on the next start).</item>
/// <item>A task from beta1.3 or older that points at a user-writable folder is moved to Program Files on the first
/// start; if that fails the task is deleted and the toggle goes off with a status line.</item>
/// <item>Registered through the Task Scheduler COM API with the XML as a string: no %TEMP% file any more.</item>
/// <item>Turning it off deletes the task and removes the Program Files copy (Amine). When the running exe is that copy,
/// its files are deleted at the next reboot (MoveFileEx) or by the next start from elsewhere.</item>
/// </list>
/// %AppData%\AlienControl (settings, logs) is never touched here.
/// </summary>
public static class Startup
{
    private const string TaskName = "Alien Controls";
    private const string OldTaskName = "AlienControl"; // name used up to 1.1.0
    private const string ExeName = "AlienControl.exe";
    private const string Marker = ".alien-controls-startup-copy";   // only a folder we installed is ever removed
    private const string RemoveMarker = ".remove-pending";

    /// <summary>2.0.0-beta1.4: what the startup check found. Status is a line for the status bar, or null.</summary>
    public sealed record Outcome(bool Enabled, string? Status);

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AlienControl");
    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    public static Task<Outcome> CheckAsync() => Task.Run(Check);
    public static Task<bool> SetEnabledAsync(bool on) => Task.Run(() => SetEnabled(on));

    /// <summary>2.0.0-beta1.4: startup check. Finishes a staged update or removal, migrates old tasks, updates the
    /// Program Files copy when a newer version runs from elsewhere, and rebuilds the task only to an admin-only path.</summary>
    public static Outcome Check()
    {
        try
        {
            FinishPending();
            if (TryGetXml(OldTaskName, out _))
            {
                Log.Info("Startup", "task from 1.1.0 or older found: moving it to the current task");
                bool ok = TryGetXml(TaskName, out _) || SetEnabled(true);
                if (ok) DeleteTask(OldTaskName);
                return new Outcome(ok, null);
            }
            if (!TryGetXml(TaskName, out var xml)) return new Outcome(false, null);

            var cmd = TaskCommand(xml);
            if (string.IsNullOrEmpty(cmd) || !IsAdminOnlyExe(cmd))
            {
                // migration: a beta1.3 (or older) task that starts the app from a user-writable folder
                Log.Info("Startup", $"task starts a non admin-only path ({cmd ?? "no command"}): moving it to {InstallDir}");
                if (Install() && Register(InstalledExe))
                {
                    Log.Info("Startup", "task moved to Program Files");
                    return new Outcome(true, "Run on startup now uses a safe copy in Program Files.");
                }
                DeleteTask(TaskName);
                Log.Info("Startup", "couldn't install to Program Files: startup task deleted, Run on startup off");
                return new Outcome(false, "Run on startup turned off: its folder could be changed without admin rights. Turn it on again to use a safe copy in Program Files.");
            }

            // a newer version run from somewhere else updates the Program Files copy
            if (File.Exists(InstalledExe) && !RunningFromInstall() && IsNewer(Environment.ProcessPath, InstalledExe))
            {
                Log.Info("Startup", $"updating {InstallDir} from {AppContext.BaseDirectory}");
                if (!Install()) Log.Info("Startup", "update of the Program Files copy failed (kept the old one)");
            }

            var target = NeedsRebuild(xml, File.Exists(InstalledExe) ? InstalledExe : cmd);
            if (target is not null && !Register(target)) Log.Info("Startup", "task rebuild failed");
            return new Outcome(true, null);
        }
        catch (Exception ex) { Log.Warn("Startup.Check", ex); return new Outcome(TryGetXml(TaskName, out _), null); }
    }

    /// <summary>Kept for callers that only need on / off.</summary>
    public static bool IsEnabled() => Check().Enabled;

    public static bool SetEnabled(bool enable)
    {
        try
        {
            if (!enable)
            {
                bool ok = DeleteTask(TaskName);
                DeleteTask(OldTaskName);
                if (ok) RemoveInstall();   // 2.0.0-beta1.4: off also removes the Program Files copy (Amine)
                return ok;
            }
            if (!Install())
            {
                Log.Info("Startup", "couldn't install to Program Files: task not registered");
                return false;
            }
            return Register(InstalledExe);
        }
        catch (Exception ex) { Log.Warn("Startup.SetEnabled", ex); return false; }
    }

    /// <summary>2.0.0-beta1.3 (services review #4), 2.0.0-beta1.4: returns the exe the task should be rebuilt to point at,
    /// or null when it needs no rebuild. exe = the wanted target (null keeps the task's own command). Rebuilt for the
    /// pre-1.3 battery conditions or a command that points somewhere else. Never returns a path that fails the
    /// admin-only check (adminOnly, default <see cref="IsAdminOnlyExe"/>), so the admin task can't follow the app
    /// into a user-writable folder.</summary>
    internal static string? NeedsRebuild(string xml, string? exe = null, Func<string, bool>? adminOnly = null)
    {
        var cmd = TaskCommand(xml);
        bool stale = xml.Contains("<DisallowStartIfOnBatteries>true", StringComparison.OrdinalIgnoreCase)
            || xml.Contains("<StopIfGoingOnBatteries>true", StringComparison.OrdinalIgnoreCase)
            || xml.Contains("<ExecutionTimeLimit>PT72H", StringComparison.OrdinalIgnoreCase)
            || !xml.Contains("<DisallowStartIfOnBatteries>", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(exe) && !SamePath(cmd, exe));
        var target = string.IsNullOrEmpty(exe) ? cmd : exe;
        if (!stale || string.IsNullOrEmpty(target)) return null;
        return (adminOnly ?? IsAdminOnlyExe)(target) ? target : null;
    }

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

    private static bool RunningFromInstall() =>
        SamePath(Path.GetDirectoryName(Environment.ProcessPath ?? ""), InstallDir);

    // ---------- admin-only check ----------

    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;
    // an ancestor only matters if it can be renamed or re-permissioned (a swapped parent swaps the whole path)
    private const FileSystemRights AncestorRights = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private const long GenericWrite = 0x40000000, GenericAll = 0x10000000;

    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private static bool AdminSid(SecurityIdentifier sid, bool creatorOwner) =>
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || sid == TrustedInstaller
        // CREATOR OWNER only hands rights to whoever creates a child, and only admins can create one here
        || (creatorOwner && sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid));

    /// <summary>2.0.0-beta1.4: true only if the folder, its tools\ folder and the exe in it (when present) grant write,
    /// append, delete, delete child, change permissions or take ownership to nobody but SYSTEM, Administrators,
    /// TrustedInstaller (and CREATOR OWNER), are owned by one of those, and no parent folder can be renamed or
    /// re-permissioned by anyone else. False on any error and off Windows.</summary>
    public static bool IsAdminOnly(string dir) => IsAdminOnly(dir, ExeName);

    internal static bool IsAdminOnlyExe(string exe)
    {
        var dir = Path.GetDirectoryName(exe);
        return !string.IsNullOrEmpty(dir) && File.Exists(exe) && IsAdminOnly(dir, Path.GetFileName(exe));
    }

    private static bool IsAdminOnly(string dir, string exeName)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var di = new DirectoryInfo(Path.GetFullPath(dir));
            if (!di.Exists) return false;
            if (!Safe(di.GetAccessControl(), WriteRights, inheritOnlyToo: true)) return false;
            var tools = new DirectoryInfo(Path.Combine(di.FullName, "tools"));
            if (tools.Exists && !Safe(tools.GetAccessControl(), WriteRights, true)) return false;
            var exe = new FileInfo(Path.Combine(di.FullName, exeName));
            if (exe.Exists && !Safe(exe.GetAccessControl(), WriteRights, false)) return false;
            for (var p = di.Parent; p is not null; p = p.Parent)
                if (!Safe(p.GetAccessControl(), AncestorRights, false)) return false;
            return true;
        }
        catch (Exception ex) { Log.Warn("Startup.IsAdminOnly", ex); return false; }
    }

    private static bool Safe(FileSystemSecurity sec, FileSystemRights rights, bool inheritOnlyToo)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (sec.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !AdminSid(owner, false)) return false;
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            // inherit-only ACEs can carry raw GENERIC_WRITE / GENERIC_ALL bits instead of the file rights
            bool writes = (r.FileSystemRights & rights) != 0 || ((long)r.FileSystemRights & (GenericWrite | GenericAll)) != 0;
            if (r.AccessControlType != AccessControlType.Allow || !writes) continue;
            if (!inheritOnlyToo && (r.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if (!AdminSid((SecurityIdentifier)r.IdentityReference, true)) return false;
        }
        return true;
    }

    // ---------- install / update / remove ----------

    /// <summary>The files the app needs next to it: the exe, its DLLs and runtime json (non single-file builds),
    /// LICENSE, README and the alienfx-tools CLIs in tools\. Nothing else from the user's folder.</summary>
    internal static List<string> AppFiles(string src)
    {
        // a zip extracted straight into Downloads must not drag the other files there along, so only named files.
        // A published build is one exe; a dev build also needs the DLLs its deps.json lists and runtimes\.
        var list = new List<string>();
        var deps = Path.Combine(src, "AlienControl.deps.json");
        string depsText = File.Exists(deps) ? File.ReadAllText(deps) : "";
        foreach (var f in Directory.EnumerateFiles(src))
        {
            var n = Path.GetFileName(f);
            if (n.Equals(ExeName, StringComparison.OrdinalIgnoreCase) || n.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
                || n.Equals("README.md", StringComparison.OrdinalIgnoreCase)
                || (depsText.Length > 0 && (n.Equals("AlienControl.deps.json", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("AlienControl.runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
                    || (n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && depsText.Contains(n, StringComparison.OrdinalIgnoreCase)))))
                list.Add(Path.GetRelativePath(src, f));
        }
        var runtimes = Path.Combine(src, "runtimes");
        if (depsText.Length > 0 && Directory.Exists(runtimes))
            foreach (var f in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
                list.Add(Path.GetRelativePath(src, f));
        var tools = Path.Combine(src, "tools");
        if (Directory.Exists(tools))
            foreach (var f in Directory.EnumerateFiles(tools, "*", SearchOption.AllDirectories))
                list.Add(Path.GetRelativePath(src, f));
        return list;
    }

    /// <summary>Copies the running app to %ProgramFiles%\AlienControl (admin-only ACL) and returns true when the
    /// folder holds the exe and passes the admin-only check.</summary>
    private static bool Install()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var dst = InstallDir;
        try
        {
            EnsureSecureDir(dst);
            CancelPendingDeletes(dst);
            try { File.Delete(Path.Combine(dst, RemoveMarker)); } catch (Exception ex) { Log.Warn("Startup.Install marker", ex); }
            var src = Path.GetFullPath(AppContext.BaseDirectory);
            if (!SamePath(src.TrimEnd('\\'), dst))
            {
                var files = AppFiles(src);
                if (!files.Any(f => f.Equals(ExeName, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Info("Startup", $"{ExeName} not found in {src}: not installed");
                    return false;
                }
                // stage everything as *.new first, then swap, so a failed copy never leaves half an update
                foreach (var rel in files)
                {
                    var to = Path.Combine(dst, rel) + ".new";
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(Path.Combine(src, rel), to, true);
                    OwnByAdmins(to);
                }
                foreach (var rel in files) Swap(Path.Combine(dst, rel));
            }
            File.WriteAllText(Path.Combine(dst, Marker), "Installed by Alien Controls for Run on startup. Turning Run on startup off removes this folder.\r\n");
            bool ok = File.Exists(InstalledExe) && IsAdminOnly(dst);
            Log.Info("Startup", ok ? $"installed to {dst}" : $"{dst} failed the admin-only check");
            return ok;
        }
        catch (Exception ex) { Log.Warn("Startup.Install", ex); return false; }
    }

    /// <summary>Moves path.new over path. A file in use (the running exe) is renamed to path.old first, which Windows
    /// allows; if even that fails the .new stays and <see cref="FinishPending"/> completes it on the next start.</summary>
    private static void Swap(string path)
    {
        var staged = path + ".new";
        if (!File.Exists(staged)) return;
        try { File.Move(staged, path, true); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            File.Move(path, path + ".old", true);
            File.Move(staged, path);
            ScheduleDelete(path + ".old");
        }
        catch (Exception ex) { Log.Warn("Startup.Swap " + Path.GetFileName(path), ex); }
    }

    /// <summary>Next start: complete a staged update, delete *.old files, and finish a removal that couldn't run
    /// because the copy itself was running.</summary>
    private static void FinishPending()
    {
        var dst = InstallDir;
        if (!Directory.Exists(dst) || !File.Exists(Path.Combine(dst, Marker))) return;
        try
        {
            if (File.Exists(Path.Combine(dst, RemoveMarker)) && !RunningFromInstall() && !TryGetXml(TaskName, out _))
            {
                RemoveInstall();
                return;
            }
            foreach (var f in Directory.EnumerateFiles(dst, "*.new", SearchOption.AllDirectories).ToList()) Swap(f[..^4]);
            foreach (var f in Directory.EnumerateFiles(dst, "*.old", SearchOption.AllDirectories).ToList())
                try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) { Log.Warn("Startup.FinishPending", ex); }
    }

    /// <summary>2.0.0-beta1.4: Run on startup off removes the Program Files copy (Amine). Only a folder this app
    /// installed (marker file). When the running exe is that copy it keeps its files for this session (it needs
    /// tools\), and everything is deleted at the next reboot or by the next start from elsewhere.</summary>
    private static void RemoveInstall()
    {
        var dst = InstallDir;
        if (!Directory.Exists(dst)) return;
        if (!File.Exists(Path.Combine(dst, Marker)))
        {
            Log.Info("Startup", $"{dst} was not installed by Alien Controls: left alone");
            return;
        }
        try
        {
            if (RunningFromInstall())
            {
                File.WriteAllText(Path.Combine(dst, RemoveMarker), "");
                foreach (var f in Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories)) ScheduleDelete(f);
                foreach (var d in Directory.EnumerateDirectories(dst, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length)) ScheduleDelete(d);
                ScheduleDelete(dst);
                Log.Info("Startup", $"{dst} is the running copy: it is removed at the next reboot");
                return;
            }
            Directory.Delete(dst, true);
            Log.Info("Startup", $"removed {dst}");
        }
        catch (Exception ex)
        {
            Log.Warn("Startup.RemoveInstall", ex);
            // something was in use: finish at the next reboot, or the next start from elsewhere
            try
            {
                if (Directory.Exists(dst))
                {
                    File.WriteAllText(Path.Combine(dst, RemoveMarker), "");
                    foreach (var f in Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories)) ScheduleDelete(f);
                    ScheduleDelete(dst);
                }
            }
            catch (Exception ex2) { Log.Warn("Startup.RemoveInstall schedule", ex2); }
        }
    }

    private static void EnsureSecureDir(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        var di = new DirectoryInfo(dir);
        if (!di.Exists) di.Create(sec);
        else di.SetAccessControl(sec);   // also resets a folder someone loosened
        foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories)) OwnByAdmins(f.FullName);
    }

    /// <summary>A file created by an elevated admin can still be owned by the user (a policy setting), and an owner can
    /// always change the ACL, so every copied file is given to Administrators.</summary>
    private static void OwnByAdmins(string file)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var fi = new FileInfo(file);
            var sec = fi.GetAccessControl(AccessControlSections.Owner);
            sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            fi.SetAccessControl(sec);
        }
        catch (Exception ex) { Log.Warn("Startup.OwnByAdmins", ex); }
    }

    // ---------- version ----------

    private static bool IsNewer(string? exe, string than)
    {
        try
        {
            if (string.IsNullOrEmpty(exe)) return false;
            var a = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
            var b = FileVersionInfo.GetVersionInfo(than).ProductVersion;
            int c = CompareVersions(a, b);
            return c > 0 || (c == 0 && new FileInfo(exe).Length != new FileInfo(than).Length);   // same version, other build
        }
        catch (Exception ex) { Log.Warn("Startup.IsNewer", ex); return false; }
    }

    /// <summary>2.0.0-beta1.4: compares "2.0.0-beta1.4" style versions (the +commit suffix ignored). A release beats
    /// its pre-releases; pre-release tags compare number by number ("beta1.10" &gt; "beta1.4"). Unparsable = oldest.</summary>
    internal static int CompareVersions(string? a, string? b)
    {
        static (Version v, string pre)? P(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Split('+')[0].Trim();
            var i = s.IndexOf('-');
            return Version.TryParse(i < 0 ? s : s[..i], out var v) ? (v, i < 0 ? "" : s[(i + 1)..]) : null;
        }
        var x = P(a); var y = P(b);
        if (x is null || y is null) return x is null ? (y is null ? 0 : -1) : 1;
        int c = x.Value.v.CompareTo(y.Value.v);
        if (c != 0) return c;
        if (x.Value.pre.Length == 0 || y.Value.pre.Length == 0)
            return x.Value.pre.Length == y.Value.pre.Length ? 0 : x.Value.pre.Length == 0 ? 1 : -1;
        var px = System.Text.RegularExpressions.Regex.Split(x.Value.pre, @"(\d+)");
        var py = System.Text.RegularExpressions.Regex.Split(y.Value.pre, @"(\d+)");
        for (int k = 0; k < Math.Min(px.Length, py.Length); k++)
        {
            c = long.TryParse(px[k], out var nx) && long.TryParse(py[k], out var ny) ? nx.CompareTo(ny)
                : string.Compare(px[k], py[k], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return Math.Sign(c);
        }
        return px.Length.CompareTo(py.Length);
    }

    // ---------- Task Scheduler (COM, late bound: no interop package) ----------

    private const int TASK_CREATE_OR_UPDATE = 6;
    private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;

    private static dynamic Folder()
    {
        var t = Type.GetTypeFromProgID("Schedule.Service", true)!;
        dynamic svc = Activator.CreateInstance(t)!;
        svc.Connect();
        return svc.GetFolder("\\");
    }

    private static bool TryGetXml(string name, out string xml)
    {
        xml = "";
        try { dynamic task = Folder().GetTask("\\" + name); xml = (string)task.Xml; return true; }
        catch (COMException) { return false; }   // not found (0x80070002) or no access
        catch (Exception ex) { Log.Warn("Startup.GetTask", ex); return false; }
    }

    /// <summary>Registers (create or update) the task from the XML string. Refuses any exe that fails the admin-only check.</summary>
    private static bool Register(string exe)
    {
        if (!IsAdminOnlyExe(exe))
        {
            Log.Info("Startup", $"{exe} is not admin-only: task not registered");
            return false;
        }
        try
        {
            var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            Folder().RegisterTask(TaskName, TaskXml(exe, user), TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN);
            Log.Info("Startup", $"task registered: {exe}");
            return true;
        }
        catch (Exception ex) { Log.Warn("Startup.Register", ex); return false; }
    }

    /// <summary>True when the task is gone afterwards (also when it never existed).</summary>
    private static bool DeleteTask(string name)
    {
        if (!TryGetXml(name, out _)) return true;
        try { Folder().DeleteTask(name, 0); Log.Info("Startup", $"task deleted: {name}"); return true; }
        catch (Exception ex) { Log.Warn("Startup.DeleteTask", ex); return false; }
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

    // ---------- delete at reboot ----------

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existing, string? replacement, int flags);

    private static void ScheduleDelete(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT))
            Log.Info("Startup", $"couldn't schedule delete of {path} (error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>Turning Run on startup on again after an off from the running Program Files copy: drop the pending
    /// reboot deletes for that folder (PendingFileRenameOperations pairs "\??\path", "" = delete).</summary>
    private static void CancelPendingDeletes(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager", true);
            if (key?.GetValue("PendingFileRenameOperations") is not string[] ops || ops.Length < 2) return;
            var prefix = @"\??\" + dir.TrimEnd('\\');
            var keep = new List<string>();
            for (int i = 0; i + 1 < ops.Length; i += 2)
            {
                bool ours = ops[i + 1].Length == 0 && (ops[i].Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || ops[i].StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase));
                bool old = ops[i].EndsWith(".old", StringComparison.OrdinalIgnoreCase);
                if (!ours || old) { keep.Add(ops[i]); keep.Add(ops[i + 1]); }
            }
            if (keep.Count == ops.Length - (ops.Length % 2)) return;
            if (keep.Count == 0) key.DeleteValue("PendingFileRenameOperations", false);
            else key.SetValue("PendingFileRenameOperations", keep.ToArray(), Microsoft.Win32.RegistryValueKind.MultiString);
            Log.Info("Startup", "cancelled the pending removal of the Program Files copy");
        }
        catch (Exception ex) { Log.Warn("Startup.CancelPendingDeletes", ex); }
    }
}
