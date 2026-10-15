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
/// GPU modes for the m15 R7 (v0.8: the UI buttons are preview only and not wired to these methods yet).
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
        catch { }
        return list;
    }

    public static GpuModeKind Current(List<Adapter> a)
    {
        var intel = a.FirstOrDefault(x => x.IsIntel);
        var nv = a.FirstOrDefault(x => x.IsNvidia);
        if (nv is null) return GpuModeKind.Unknown;
        if (intel is null || !intel.Ok) return GpuModeKind.Ultimate; // BIOS hid the iGPU: MUX on
        if (nv.Disabled) return GpuModeKind.IGpuOnly;
        if (InternalPanelOnNvidia() == true) return GpuModeKind.DGpuOnly; // Advanced Optimus "NVIDIA GPU only"
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

    /// <summary>True when an NVIDIA adapter (VEN_10DE) drives the built-in panel, null if unknown.</summary>
    public static bool? InternalPanelOnNvidia()
    {
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return null;
            var paths = new PATH_INFO[np];
            var modes = new MODE_INFO[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return null;
            for (int i = 0; i < np; i++)
            {
                uint tech = paths[i].target.outputTechnology;
                if (tech != OUTPUT_INTERNAL && tech != OUTPUT_LVDS && tech != OUTPUT_DP_EMBEDDED && tech != OUTPUT_UDI_EMBEDDED) continue;
                var name = new ADAPTER_NAME { type = 4, size = (uint)Marshal.SizeOf<ADAPTER_NAME>(), adapterId = paths[i].target.adapterId };
                if (DisplayConfigGetDeviceInfo(ref name) != 0) return null;
                return name.adapterDevicePath.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Opens the NVIDIA Control Panel (Display > Manage display mode lives there). Not wired in v0.8.</summary>
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

    /// <summary>Processes holding dedicated GPU memory (the RTX; the Intel iGPU uses shared memory).</summary>
    public static List<string> AppsUsingDgpu()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cat = new PerformanceCounterCategory("GPU Process Memory");
            int self = Environment.ProcessId;
            foreach (var inst in cat.GetInstanceNames())
            {
                // pid_1234_luid_0x..._phys_0
                var parts = inst.Split('_');
                if (parts.Length < 2 || parts[0] != "pid" || !int.TryParse(parts[1], out int pid) || pid == self) continue;
                using var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, true);
                if (c.RawValue < 16L * 1024 * 1024) continue;
                try
                {
                    var p = Process.GetProcessById(pid).ProcessName;
                    if (!p.Equals("dwm", StringComparison.OrdinalIgnoreCase)) names.Add(p);
                }
                catch { }
            }
        }
        catch { }
        return names.ToList();
    }

    private static async Task<(bool Ok, string Output)> PnpUtil(string verb, string id)
    {
        try
        {
            var psi = new ProcessStartInfo("pnputil.exe", $"/{verb} \"{id}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            string o = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode == 0 || p.ExitCode == 3010, o.Trim());
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
        foreach (var a in DellBios.EnumAttributes())
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
        return DellBios.SetAttribute(attr.Name, value);
    }

    /// <summary>Text report of display adapters and BIOS attributes, copied for debugging.</summary>
    public static string Report()
    {
        var sb = new StringBuilder("Alien Controls GPU report\n");
        foreach (var a in Adapters()) sb.AppendLine($"Adapter: {a.Name} | code {a.ErrorCode} | {a.PnpId}");
        sb.AppendLine($"Mode: {Current(Adapters())}");
        sb.AppendLine($"Internal panel on NVIDIA (Advanced Optimus 'NVIDIA GPU only'): {InternalPanelOnNvidia()?.ToString() ?? "unknown"}");
        var g = FindGraphicsAttribute();
        sb.AppendLine(g is null
            ? "Ultimate (MUX) from Windows: NOT possible - no hybrid/MUX attribute in Dell BIOS WMI."
            : $"Ultimate (MUX) from Windows: possible via '{g.Name}' = {g.Current} [{string.Join("/", g.Possible)}]");
        var x = DellBios.FindExpressCharge();
        sb.AppendLine(x is null ? "ExpressCharge: not exposed" : $"ExpressCharge: '{x.Name}' = {x.Current} [{string.Join("/", x.Possible)}]");
        sb.AppendLine($"BIOS passwords: {DellBios.PasswordState() ?? "unknown (wmisecurity not exposed)"}");
        sb.AppendLine(PowerMeter.Describe());
        try
        {
            using var s = new ManagementObjectSearcher(BiosNs, "SELECT * FROM EnumerationAttribute");
            int n = 0;
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    n++;
                    sb.AppendLine($"BIOS: {o["AttributeName"]} = {DellBios.First(o["CurrentValue"])} [{string.Join("/", (o["PossibleValue"] as string[]) ?? Array.Empty<string>())}]");
                }
            if (n == 0) sb.AppendLine("BIOS WMI: no attributes");
        }
        catch (Exception ex) { sb.AppendLine("BIOS WMI: " + ex.Message); }
        return sb.ToString();
    }
}
