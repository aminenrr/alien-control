using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace AlienControl.Services;

/// <summary>
/// Dell BIOS WMI (root\dcim\sysman\biosattributes): read enumeration attributes and set one.
/// Used for Ultimate/MUX (GpuMode) and the experimental ExpressCharge toggle.
/// </summary>
public static class DellBios
{
    public const string Namespace = @"root\dcim\sysman\biosattributes";

    public sealed record Attr(string Name, string Current, string[] Possible);

    public static List<Attr> EnumAttributes()
    {
        var list = new List<Attr>();
        try
        {
            using var s = new ManagementObjectSearcher(Namespace, "SELECT * FROM EnumerationAttribute");
            foreach (ManagementObject o in s.Get())
                using (o)
                    list.Add(new Attr(o["AttributeName"]?.ToString() ?? "", First(o["CurrentValue"]),
                        (o["PossibleValue"] as string[]) ?? Array.Empty<string>()));
        }
        catch { }
        return list;
    }

    /// <summary>CurrentValue comes back as a string on some BIOSes and as string[] on others (v0.8.5 fix:
    /// the v0.8.0 report showed it empty because only string[] was read).</summary>
    public static string First(object? v) => v switch
    {
        string[] a => a.FirstOrDefault() ?? "",
        string s => s,
        null => "",
        _ => v.ToString() ?? ""
    };

    /// <summary>Whether a BIOS Admin / System password is set (root\dcim\sysman\wmisecurity PasswordObject).
    /// Writes need it when set. Returns "Admin: no, System: no" style text, or null if the class isn't there.</summary>
    public static string? PasswordState()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\dcim\sysman\wmisecurity", "SELECT * FROM PasswordObject");
            var parts = new List<string>();
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    var set = Convert.ToUInt32(o["IsPasswordSet"] ?? 0u) != 0;
                    parts.Add($"{o["NameId"]}: {(set ? "SET" : "no")}");
                }
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
        catch { return null; }
    }

    /// <summary>Sets a BIOS attribute. Returns null on success or an error message.</summary>
    public static string? SetAttribute(string name, string value)
    {
        try
        {
            using var cls = new ManagementClass(Namespace, "BIOSAttributeInterface", null);
            foreach (ManagementObject inst in cls.GetInstances())
                using (inst)
                {
                    var p = inst.GetMethodParameters("SetAttribute");
                    p["SecType"] = 0u;
                    p["SecHndCount"] = 0u;
                    p["SecHandle"] = Array.Empty<byte>();
                    p["AttributeName"] = name;
                    p["AttributeValue"] = value;
                    var r = inst.InvokeMethod("SetAttribute", p, null);
                    uint status = Convert.ToUInt32(r?["Status"] ?? r?["ReturnValue"] ?? 1u);
                    return status == 0 ? null : $"BIOS refused the change (status {status}). A BIOS admin password blocks it.";
                }
        }
        catch (Exception ex) { return "BIOS WMI error: " + ex.Message; }
        return "BIOS WMI interface not found.";
    }

    // ---------- ExpressCharge (experimental) ----------

    /// <summary>
    /// The battery charge attribute that offers Express charging: usually PrimaryBattChargeCfg
    /// (Adaptive / Standard / Express / PrimAcUse / Custom), or a plain ExpressCharge Enabled/Disabled.
    /// Null when the BIOS doesn't expose it.
    /// </summary>
    public static Attr? FindExpressCharge()
    {
        foreach (var a in EnumAttributes())
        {
            string key = a.Name.Replace(" ", "").ToLowerInvariant();
            if (!(key.Contains("battchargecfg") || key.Contains("expresscharge") || key.Contains("chargemode"))) continue;
            if (a.Possible.Any(IsExpressValue) || (key.Contains("expresscharge") && a.Possible.Any(v => v.StartsWith("Enable", StringComparison.OrdinalIgnoreCase))))
                return a;
        }
        return null;
    }

    private static bool IsExpressValue(string v) => v.Contains("Express", StringComparison.OrdinalIgnoreCase);

    public static bool IsExpressOn(Attr a) =>
        a.Name.Contains("ExpressCharge", StringComparison.OrdinalIgnoreCase) && !a.Possible.Any(IsExpressValue)
            ? a.Current.StartsWith("Enable", StringComparison.OrdinalIgnoreCase)
            : IsExpressValue(a.Current);

    /// <summary>Turns Express charging on, or back to <paramref name="restore"/> (else Adaptive/Standard).</summary>
    public static string? SetExpressCharge(Attr a, bool on, string? restore)
    {
        string? value;
        if (a.Name.Contains("ExpressCharge", StringComparison.OrdinalIgnoreCase) && !a.Possible.Any(IsExpressValue))
            value = a.Possible.FirstOrDefault(v => v.StartsWith(on ? "Enable" : "Disable", StringComparison.OrdinalIgnoreCase));
        else if (on)
            value = a.Possible.FirstOrDefault(IsExpressValue);
        else
            value = new[] { restore, "Adaptive", "Standard" }
                .FirstOrDefault(c => c is not null && !IsExpressValue(c) && a.Possible.Contains(c, StringComparer.OrdinalIgnoreCase))
                ?? a.Possible.FirstOrDefault(v => !IsExpressValue(v) && !v.Contains("Custom", StringComparison.OrdinalIgnoreCase));
        if (value is null) return $"Unknown values for {a.Name}: {string.Join(", ", a.Possible)}";
        if (value.Equals(a.Current, StringComparison.OrdinalIgnoreCase)) return null;
        return SetAttribute(a.Name, value);
    }
}
