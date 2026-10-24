using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AlienControl.Services;

/// <summary>
/// v0.8 names. IGpuOnly = the old Eco (RTX disabled), Optimus = the old Standard (hybrid),
/// DGpuOnly = NVIDIA Advanced Optimus display mode "NVIDIA GPU only" (RTX drives the panel, no reboot),
/// Ultimate = MUX through Dell BIOS WMI (restart). Ultimate is manual only: nothing switches to it automatically.
/// </summary>
public enum GpuModeKind { Unknown, IGpuOnly, Optimus, DGpuOnly, Ultimate }

/// <summary>
/// GPU modes for the m15 R7 (1.2.0-beta.2: iGPU only / Advanced Optimus / Ultimate in the UI).
///   iGPU only = NVIDIA display device disabled (pnputil), Intel drives the screen. Hybrid mode only.
///   Optimus   = hybrid: both GPUs on, Intel drives the screen, RTX wakes on demand.
///   dGPU only = NVIDIA Advanced Optimus "NVIDIA GPU only" display mode. NVIDIA has no API for it, so the app
///               detects it (which adapter drives the internal panel) and can open NVIDIA's settings.
///   Ultimate  = MUX: a BIOS setting, needs a restart. The AWCC WMI interface has no MUX method on this laptop,
///               so the app looks for the graphics attribute in Dell's BIOS WMI. If it isn't there, Ultimate
///               can't be toggled from Windows; "Check Ultimate support" copies a report.
/// </summary>
public static class GpuMode
{
    public sealed record Adapter(string Name, string PnpId, int ErrorCode)
    {
        public bool IsIntel => Name.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        public bool IsNvidia => Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        public bool Disabled => ErrorCode == 22;
        public bool Ok => ErrorCode == 0;
    }

    public static List<Adapter> Adapters()
    {
        var list = new List<Adapter>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass='Display'");
            foreach (ManagementObject o in s.Get())
                using (o)
                    list.Add(new Adapter(o["Name"]?.ToString() ?? "", o["PNPDeviceID"]?.ToString() ?? "",
                        Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0)));
        }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("GpuMode.cs:50", __e); }
        return list;
    }

    /// <summary>
    /// v0.9.0: full GPU state for the row. Kind comes from the adapters + which GPU drives the panel (live, works with any
    /// driver). Auto = NVIDIA "Automatic Select", read from the flag NVIDIA writes after every switch (Procmon 2026-10-15).
    /// BiosUltimate = what the BIOS will boot into (null if unreadable); differs from Kind == Ultimate until a restart.
    /// </summary>
    public sealed record State(GpuModeKind Kind, bool Auto, bool? BiosUltimate)
    {
        public bool PendingRestart => BiosUltimate is bool b && b != (Kind == GpuModeKind.Ultimate);
    }

    private const string AceKey = @"SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\Global\NvHybrid\Persistence\ACE";

    /// <summary>NVIDIA's own record of the display mode. Null values when the key isn't there (another driver layout).</summary>
    public static (bool? Auto, int? MuxState) ReadNvidiaFlags()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(AceKey);
            if (k is null) return (null, null);
            int? auto = k.GetValue("InternalMuxIsAutomaticMode") is int a ? a : null;
            int? mux = k.GetValue("InternalMuxState") is int m ? m : null;
            return (auto is null ? null : auto == 1, mux);
        }
        catch { return (null, null); }
    }

    /// <summary>Reads everything. withBios = also ask the BIOS (slower, only on window show and after a change).</summary>
    /// <param name="switchSetUtc">When the app last wrote the BIOS switch (Settings.GpuSwitchSetUtc).</param>
    /// <summary>Windows' boot counter (Memory Management\PrefetchParameters\BootId), or null.</summary>
    public static int? BootId()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
            return k?.GetValue("BootId") is int v ? v : null;
        }
        catch (Exception ex) { Log.Warn("BootId", ex); return null; }
    }

    public static State Read(bool withBios, DateTime? switchSetUtc = null, int? switchSetBootId = null)
    {
        var kind = Current(Adapters());
        bool? bios = null;
        if (withBios)
        {
            var attr = FindGraphicsAttribute();
            if (attr is not null)
            {
                bool on = attr.Current.StartsWith("Enable", StringComparison.OrdinalIgnoreCase) || attr.Current.Equals("On", StringComparison.OrdinalIgnoreCase);
                bios = attr.MuxMeansOn ? on : !on;
            }
        }
        if (bios is bool b)
        {
            // v0.9.3: on the m15 R7 the Intel GPU stays listed (and OK) in Ultimate, so the adapters can't tell
            // Ultimate from NVIDIA GPU only. The BIOS value can: it's what this boot used, unless the app changed it
            // after the boot (then the running mode is the other one).
            // 1.3.0-beta.1 (review #11): TickCount64 doesn't reset on a Fast Startup "shut down", so the boot is
            // identified by Windows' BootId (bumped on every boot, Fast Startup included), saved at switch time.
            // Falls back to the tick count when either id is missing.
            bool changedSinceBoot;
            if (switchSetBootId is int id && BootId() is int now) changedSinceBoot = switchSetUtc is not null && id == now;
            else
            {
                var bootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
                changedSinceBoot = switchSetUtc is DateTime t && t > bootUtc;
            }
            bool runningUlt = changedSinceBoot ? !b : b;
            if (runningUlt) kind = GpuModeKind.Ultimate;
            else if (kind == GpuModeKind.Ultimate) kind = GpuModeKind.DGpuOnly;
        }
        bool auto = kind is GpuModeKind.Optimus or GpuModeKind.DGpuOnly && ReadNvidiaFlags().Auto == true;
        return new State(kind, auto, bios);
    }

    /// <summary>Restarts Windows now (after a BIOS graphics change).</summary>
    public static void RestartNow()
    {
        try { Process.Start(new ProcessStartInfo(SysExe.Get("shutdown.exe"), "/r /t 0") { CreateNoWindow = true, UseShellExecute = false }); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("GpuMode.cs:132", __e); }
    }

    public static GpuModeKind Current(List<Adapter> a) => Current(a, null, probePanel: true);

    /// <summary>1.3.0-beta.1: testable core. panelOnNvidia = what the panel probe said (probePanel=false skips the probe).</summary>
    internal static GpuModeKind Current(List<Adapter> a, bool? panelOnNvidia, bool probePanel)
    {
        var intel = a.FirstOrDefault(x => x.IsIntel);
        var nv = a.FirstOrDefault(x => x.IsNvidia);
        if (nv is null) return GpuModeKind.Unknown;
        if (intel is null || !intel.Ok) return GpuModeKind.Ultimate; // BIOS hid the iGPU: MUX on
        if (nv.Disabled) return GpuModeKind.IGpuOnly;
        var panel = probePanel ? InternalPanelOnNvidia() : panelOnNvidia;
        if (panel is null && probePanel) panel = ReadNvidiaFlags().MuxState is int m ? m == 2 : null;   // fallback: NVIDIA's record (2 = RTX)
        if (panel == true) return GpuModeKind.DGpuOnly; // Advanced Optimus "NVIDIA GPU only" (or Auto with the RTX on the panel)
        return GpuModeKind.Optimus;
    }

    // ---------- which adapter drives the internal panel (QueryDisplayConfig) ----------

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET
    {
        public LUID adapterId; public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public uint refreshNum, refreshDen, scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO { public PATH_SOURCE source; public PATH_TARGET target; public uint flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct MODE_INFO { public uint infoType, id; public LUID adapterId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ADAPTER_NAME
    {
        public uint type, size; public LUID adapterId; public uint id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string adapterDevicePath;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes, [Out] MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref ADAPTER_NAME info);

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint OUTPUT_INTERNAL = 0x80000000, OUTPUT_LVDS = 6, OUTPUT_DP_EMBEDDED = 11, OUTPUT_UDI_EMBEDDED = 13;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_NAME
    {
        public uint type, size; public LUID adapterId; public uint id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME info);

    /// <summary>2.0.0-beta1.3: the active path that drives the built-in panel (internal output technology), shared by
    /// InternalPanelOnNvidia and InternalPanelGdiName. False = no internal panel active (lid closed), null = unknown.</summary>
    private static bool? FindInternalPath(out PATH_INFO path)
    {
        path = default;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return null;
        var paths = new PATH_INFO[np];
        var modes = new MODE_INFO[nm];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return null;
        for (int i = 0; i < np; i++)
        {
            uint tech = paths[i].target.outputTechnology;
            if (tech != OUTPUT_INTERNAL && tech != OUTPUT_LVDS && tech != OUTPUT_DP_EMBEDDED && tech != OUTPUT_UDI_EMBEDDED) continue;
            path = paths[i];
            return true;
        }
        return false;
    }

    /// <summary>True when an NVIDIA adapter (VEN_10DE) drives the built-in panel, null if unknown.</summary>
    public static bool? InternalPanelOnNvidia()
    {
        try
        {
            if (FindInternalPath(out var path) != true) return null;
            var name = new ADAPTER_NAME { type = 4, size = (uint)Marshal.SizeOf<ADAPTER_NAME>(), adapterId = path.target.adapterId };
            if (DisplayConfigGetDeviceInfo(ref name) != 0) return null;
            return name.adapterDevicePath.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("GpuMode.cs:196", __e); }
        return null;
    }

    /// <summary>2.0.0-beta1.3 (services review #6): GDI name (\\.\DISPLAYn) of the built-in panel, for the refresh
    /// rate. Null when no internal panel is active or it can't be read.</summary>
    public static string? InternalPanelGdiName()
    {
        try
        {
            if (FindInternalPath(out var path) != true) return null;
            var name = new SOURCE_NAME { type = 1, size = (uint)Marshal.SizeOf<SOURCE_NAME>(), adapterId = path.source.adapterId, id = path.source.id };
            if (DisplayConfigGetDeviceInfo(ref name) != 0) return null;
            return string.IsNullOrEmpty(name.viewGdiDeviceName) ? null : name.viewGdiDeviceName;
        }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("GpuMode.InternalPanelGdiName", __e); }
        return null;
    }

    /// <summary>Opens the NVIDIA Control Panel (Display > Manage display mode lives there). NVIDIA has no API for the switch.</summary>
    public static string? OpenNvidiaDisplayMode()
    {
        try
        {
            string legacy = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "Control Panel Client", "nvcplui.exe");
            var target = System.IO.File.Exists(legacy) ? legacy
                : @"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel";
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) { return "Couldn't open the NVIDIA Control Panel: " + ex.Message; }
    }

    /// <summary>Processes holding dedicated GPU memory (the RTX; the Intel iGPU uses shared memory), each with its
    /// dedicated memory in MB, biggest first (1.2.0-beta.2). 2.0.0-beta1.4: the names-only AppsUsingDgpu and the
    /// debug Report() are gone (no caller).</summary>
    public static List<(string Name, long Mb)> AppsUsingDgpuDetailed()
    {
        var by = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cat = new PerformanceCounterCategory("GPU Process Memory");
            int self = Environment.ProcessId;
            foreach (var inst in cat.GetInstanceNames())
            {
                var parts = inst.Split('_');
                if (parts.Length < 2 || parts[0] != "pid" || !int.TryParse(parts[1], out int pid) || pid == self) continue;
                using var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, true);
                long bytes = c.RawValue;
                if (bytes < 16L * 1024 * 1024) continue;
                try
                {
                    var p = Process.GetProcessById(pid).ProcessName;
                    if (p.Equals("dwm", StringComparison.OrdinalIgnoreCase)) continue;
                    by[p] = (by.TryGetValue(p, out var v) ? v : 0) + bytes;
                }
                catch (System.Exception __e) { AlienControl.Services.Log.Warn("GpuMode.cs:241", __e); }
            }
        }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("GpuMode.cs:244", __e); }
        return by.OrderByDescending(x => x.Value).Select(x => (x.Key, x.Value / (1024 * 1024))).ToList();
    }

    /// <summary>After the RTX comes back: restarts NVIDIA's Display Container service so its tray icon and
    /// display-mode popup return without a reboot. Returns a short outcome for the log.
    /// 2.0.0-beta1.4 (Amine, bug: the icon never came back after iGPU only, 1.3.x brought it back): beta1.3 ran
    /// "net stop" through ProcessRunner with kill:true and 60 s, then "net start" right away. The container's stop is slow
    /// while the driver is still coming up; when it ran past 60 s net.exe was killed mid-stop and "net start" hit a
    /// service still in STOP_PENDING and failed, so the service ended up stopped until a reboot (1.3.x waited for net
    /// stop however long it took). It was also skipped whenever any nvcontainer ran in the session, and a stale one can
    /// outlive its icon. Now: wait for the RTX to report OK, then always stop the service through the service manager
    /// (no net.exe to kill), wait until it is really STOPPED, start it, wait for RUNNING, and log what happened.
    /// Blocking (up to about 2 minutes in the worst case): call it off the UI thread and outside the command gate.</summary>
    public static async Task<string> EnsureNvidiaTrayAsync()
    {
        const string svc = "NVDisplay.ContainerLocalSystem";
        try
        {
            // 1. the RTX must be up first (pnputil returns before the driver has finished starting)
            var sw = Stopwatch.StartNew();
            while (!(Adapters().FirstOrDefault(x => x.IsNvidia)?.Ok ?? false))
            {
                if (sw.Elapsed.TotalSeconds > 30) return Done("RTX not ready after 30 s, service not restarted");
                await Task.Delay(1000).ConfigureAwait(false);
            }
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return Done($"service manager: error {Marshal.GetLastWin32Error()}");
            try
            {
                IntPtr h = OpenService(scm, svc, SERVICE_QUERY_STATUS | SERVICE_STOP | SERVICE_START);
                if (h == IntPtr.Zero) return Done($"{svc} not opened: error {Marshal.GetLastWin32Error()}");
                try
                {
                    // 2. let a start or stop already in progress settle
                    await WaitState(h, s => s is SERVICE_RUNNING or SERVICE_STOPPED or SERVICE_PAUSED, 30).ConfigureAwait(false);
                    // 3. stop, and wait until it is really stopped
                    if (SvcState(h) != SERVICE_STOPPED)
                    {
                        var st = new SERVICE_STATUS();
                        if (!ControlService(h, SERVICE_CONTROL_STOP, ref st))
                        {
                            int e = Marshal.GetLastWin32Error();
                            if (e == ERROR_DEPENDENT_SERVICES_RUNNING)
                            {
                                // dependents: net stop /y stops them too. Never killed: it is left to finish.
                                var r = await ProcessRunner.RunAsync(SysExe.Get("net.exe"), $"stop {svc} /y", 90000, kill: false).ConfigureAwait(false);
                                Log.Info("NvidiaTray", $"net stop /y: {(r.TimedOut ? "still running" : $"exit {r.ExitCode}")} {r.Output}");
                            }
                            else if (e != ERROR_SERVICE_NOT_ACTIVE) Log.Info("NvidiaTray", $"stop: error {e}");
                        }
                        if (!await WaitState(h, s => s == SERVICE_STOPPED, 90).ConfigureAwait(false))
                            return Done($"{svc} didn't stop in 90 s (state {SvcState(h)}), not started again");
                    }
                    // 4. start, and wait until it runs
                    if (!StartService(h, 0, null) && Marshal.GetLastWin32Error() is int se && se != ERROR_SERVICE_ALREADY_RUNNING)
                        return Done($"{svc} start: error {se}");
                    if (!await WaitState(h, s => s == SERVICE_RUNNING, 30).ConfigureAwait(false))
                        return Done($"{svc} not running 30 s after start (state {SvcState(h)})");
                }
                finally { CloseServiceHandle(h); }
            }
            finally { CloseServiceHandle(scm); }
            // 5. the tray process is started by the service in this session a few seconds later. 2.0.0-beta1.5 (Amine's
            // process list): the display container's own process is NVDisplay.Container.exe (driver store); nvcontainer.exe
            // is the NVIDIA app's container and runs either way, so it said "up" before the new one existed. Only a
            // process started after the restart counts.
            int session = Process.GetCurrentProcess().SessionId;
            var restartedAt = DateTime.Now.AddSeconds(-1);
            for (int i = 0; i < 15; i++)
            {
                if (Process.GetProcessesByName("NVDisplay.Container").Any(p =>
                    { try { return p.SessionId == session && p.StartTime >= restartedAt; } catch { return false; } }))
                    return Done($"{svc} restarted, tray process up after {sw.Elapsed.TotalSeconds:0} s");
                await Task.Delay(1000).ConfigureAwait(false);
            }
            return Done($"{svc} restarted, but no new NVDisplay.Container in this session after 15 s");
        }
        catch (Exception ex) { Log.Warn("NvidiaTray", ex); return "failed: " + ex.Message; }

        static string Done(string what) { Log.Info("NvidiaTray", what); return what; }
    }

    private static int SvcState(IntPtr h)
    {
        var st = new SERVICE_STATUS();
        return QueryServiceStatus(h, ref st) ? st.dwCurrentState : 0;
    }

    private static async Task<bool> WaitState(IntPtr h, Func<int, bool> ok, int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (ok(SvcState(h))) return true;
            if (sw.Elapsed.TotalSeconds > seconds) return false;
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    private const uint SC_MANAGER_CONNECT = 0x1, SERVICE_QUERY_STATUS = 0x4, SERVICE_START = 0x10, SERVICE_STOP = 0x20;
    private const int SERVICE_CONTROL_STOP = 1;
    private const int SERVICE_STOPPED = 1, SERVICE_RUNNING = 4, SERVICE_PAUSED = 7;
    private const int ERROR_DEPENDENT_SERVICES_RUNNING = 1051, ERROR_SERVICE_NOT_ACTIVE = 1062, ERROR_SERVICE_ALREADY_RUNNING = 1056;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenSCManager(string? machine, string? db, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(IntPtr h, int control, ref SERVICE_STATUS status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr h, ref SERVICE_STATUS status);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool StartService(IntPtr h, int argc, string[]? argv);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CloseServiceHandle(IntPtr h);

    /// <summary>2.0.0-beta1.3 (services review #7): how long the RTX switch waits for pnputil. After that the app stops
    /// waiting and reports it, but pnputil is never killed mid-operation: PnpIdle completes once it has exited.</summary>
    internal const int PnpTimeoutMs = 60000;

    /// <summary>Completes when no pnputil run started by the app is still going (one that timed out may still be).</summary>
    public static Task PnpIdle { get; private set; } = Task.CompletedTask;

    private static async Task<(bool Ok, string Output)> PnpUtil(string verb, string id)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(SysExe.Get("pnputil.exe"), $"/{verb} \"{id}\"", PnpTimeoutMs, kill: false);
            if (!r.Started) return (false, r.Output);
            if (r.TimedOut)
            {
                PnpIdle = r.Pending;
                return (false, $"no answer after {PnpTimeoutMs / 1000} s. It may still finish: check again in a minute, or restart if the GPU row stays wrong.");
            }
            return (r.ExitCode == 0 || r.ExitCode == 3010, r.Output);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static async Task<string?> SetDgpuEnabledAsync(bool enabled)
    {
        var nv = Adapters().FirstOrDefault(x => x.IsNvidia);
        if (nv is null) return "NVIDIA GPU not found.";
        if (enabled ? nv.Ok : nv.Disabled) return null;
        var (ok, output) = await PnpUtil(enabled ? "enable-device" : "disable-device", nv.PnpId);
        return ok ? null : $"pnputil failed: {output}";
    }

    // ---------- Dell BIOS WMI (Ultimate / MUX) ----------

    private const string BiosNs = DellBios.Namespace;

    public sealed record BiosAttr(string Name, string Current, string[] Possible, bool MuxMeansOn);

    private static readonly string[] HybridWords = { "hybrid", "switchable", "optimus" };
    private static readonly string[] MuxWords = { "mux", "discretegraphics", "dgpuonly", "discrete" };

    /// <summary>Finds the BIOS attribute that controls hybrid graphics / the MUX, if Dell exposes one.</summary>
    public static BiosAttr? FindGraphicsAttribute()
    {
        foreach (var a in HardwareController.Run(DellBios.EnumAttributes))
        {
            string key = a.Name.Replace(" ", "").ToLowerInvariant();
            bool hybrid = HybridWords.Any(key.Contains);
            bool mux = !hybrid && (key.Contains("mux") || (MuxWords.Any(key.Contains) && key.Contains("graph")));
            if (!hybrid && !mux) continue;
            return new BiosAttr(a.Name, a.Current, a.Possible, MuxMeansOn: mux);
        }
        return null;
    }

    private static string? Pick(string[] possible, bool on)
    {
        string[] want = on ? new[] { "Enabled", "Enable", "On" } : new[] { "Disabled", "Disable", "Off" };
        return possible.FirstOrDefault(p => want.Any(w => p.Equals(w, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Sets dGPU-only (MUX) on or off in BIOS. Takes effect after a restart. Manual use only.</summary>
    public static string? SetUltimate(bool ultimate)
    {
        var attr = FindGraphicsAttribute();
        if (attr is null) return "This BIOS doesn't expose the graphics switch to Windows.";
        // Hybrid attribute: Ultimate = hybrid OFF. MUX attribute: Ultimate = MUX ON.
        var value = Pick(attr.Possible, attr.MuxMeansOn ? ultimate : !ultimate);
        if (value is null) return $"Unknown values for {attr.Name}: {string.Join(", ", attr.Possible)}";
        if (value.Equals(attr.Current, StringComparison.OrdinalIgnoreCase)) return null;
        return HardwareController.Run(() => DellBios.SetAttribute(attr.Name, value));
    }
}
