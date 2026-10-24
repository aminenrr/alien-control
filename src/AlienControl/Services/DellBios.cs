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
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("DellBios.cs:29", __e); }
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
}
