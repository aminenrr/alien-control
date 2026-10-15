using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;

namespace AlienControl.Services;

public enum GpuModeKind { Unknown, Eco, Standard, Ultimate }

/// <summary>
/// G-Helper style GPU modes for the m15 R7.
///   Eco      = NVIDIA display device disabled (pnputil), Intel drives the screen. Hybrid mode only.
///   Standard = hybrid: both GPUs on, Intel drives the screen, RTX wakes on demand.
///   Ultimate = MUX / dGPU only: a BIOS setting, needs a restart. The AWCC WMI interface has no MUX
///              method on this laptop (checked with alienfan-cli dump), so the app looks for the
///              graphics attribute in Dell's BIOS WMI (root\dcim\sysman\biosattributes). If it isn't
///              there, Ultimate stays disabled and the "BIOS report" can be copied for debugging.
///   Optimized (in MainWindow) = Eco on battery, Standard on AC.
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
        if (nv.Disabled) return GpuModeKind.Eco;
        return GpuModeKind.Standard;
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

    private const string BiosNs = @"root\dcim\sysman\biosattributes";

    public sealed record BiosAttr(string Name, string Current, string[] Possible, bool MuxMeansOn);

    private static readonly string[] HybridWords = { "hybrid", "switchable", "optimus" };
    private static readonly string[] MuxWords = { "mux", "discretegraphics", "dgpuonly", "discrete" };

    /// <summary>Finds the BIOS attribute that controls hybrid graphics / the MUX, if Dell exposes one.</summary>
    public static BiosAttr? FindGraphicsAttribute()
    {
        try
        {
            using var s = new ManagementObjectSearcher(BiosNs, "SELECT * FROM EnumerationAttribute");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string name = o["AttributeName"]?.ToString() ?? "";
                    string key = name.Replace(" ", "").ToLowerInvariant();
                    bool hybrid = HybridWords.Any(key.Contains);
                    bool mux = !hybrid && (key.Contains("mux") || (MuxWords.Any(key.Contains) && key.Contains("graph")));
                    if (!hybrid && !mux) continue;
                    var cur = (o["CurrentValue"] as string[])?.FirstOrDefault() ?? "";
                    var pos = (o["PossibleValue"] as string[]) ?? Array.Empty<string>();
                    return new BiosAttr(name, cur, pos, MuxMeansOn: mux);
                }
        }
        catch { }
        return null;
    }

    private static string? Pick(string[] possible, bool on)
    {
        string[] want = on ? new[] { "Enabled", "Enable", "On" } : new[] { "Disabled", "Disable", "Off" };
        return possible.FirstOrDefault(p => want.Any(w => p.Equals(w, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Sets dGPU-only (MUX) on or off in BIOS. Takes effect after a restart.</summary>
    public static string? SetUltimate(bool ultimate)
    {
        var attr = FindGraphicsAttribute();
        if (attr is null) return "This BIOS doesn't expose the graphics switch to Windows.";
        // Hybrid attribute: Ultimate = hybrid OFF. MUX attribute: Ultimate = MUX ON.
        var value = Pick(attr.Possible, attr.MuxMeansOn ? ultimate : !ultimate);
        if (value is null) return $"Unknown values for {attr.Name}: {string.Join(", ", attr.Possible)}";
        if (value.Equals(attr.Current, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var cls = new ManagementClass(BiosNs, "BIOSAttributeInterface", null);
            foreach (ManagementObject inst in cls.GetInstances())
                using (inst)
                {
                    var p = inst.GetMethodParameters("SetAttribute");
                    p["SecType"] = 0u;
                    p["SecHndCount"] = 0u;
                    p["SecHandle"] = Array.Empty<byte>();
                    p["AttributeName"] = attr.Name;
                    p["AttributeValue"] = value;
                    var r = inst.InvokeMethod("SetAttribute", p, null);
                    uint status = Convert.ToUInt32(r?["Status"] ?? r?["ReturnValue"] ?? 1u);
                    return status == 0 ? null : $"BIOS refused the change (status {status}). A BIOS admin password blocks it.";
                }
        }
        catch (Exception ex) { return "BIOS WMI error: " + ex.Message; }
        return "BIOS WMI interface not found.";
    }

    /// <summary>Text report of display adapters and BIOS attributes, copied for debugging.</summary>
    public static string Report()
    {
        var sb = new StringBuilder("Alien Controls GPU report\n");
        foreach (var a in Adapters()) sb.AppendLine($"Adapter: {a.Name} | code {a.ErrorCode} | {a.PnpId}");
        sb.AppendLine($"Mode: {Current(Adapters())}");
        try
        {
            using var s = new ManagementObjectSearcher(BiosNs, "SELECT * FROM EnumerationAttribute");
            int n = 0;
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    n++;
                    sb.AppendLine($"BIOS: {o["AttributeName"]} = {(o["CurrentValue"] as string[])?.FirstOrDefault()} [{string.Join("/", (o["PossibleValue"] as string[]) ?? Array.Empty<string>())}]");
                }
            if (n == 0) sb.AppendLine("BIOS WMI: no attributes");
        }
        catch (Exception ex) { sb.AppendLine("BIOS WMI: " + ex.Message); }
        return sb.ToString();
    }
}
