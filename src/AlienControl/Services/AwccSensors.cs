using System;
using System.Collections.Generic;
using System.Management;

namespace AlienControl.Services;

/// <summary>
/// v1.1: temps and fan RPMs read straight from the Alienware WMI (AWCCWmiMethodFunction.Thermal_Information),
/// read-only, no alienfan-cli process per poll. Probed on the m15 R7 (dev/wmi-probe.ps1):
/// sysdesc 0x05000202 = 2 fans, 2 temp sensors; ids 0x32 CPU fan, 0x33 GPU fan, 0x01 CPU temp, 0x06 GPU temp.
/// Arg = op | id &lt;&lt; 8. Ops: 2 system description, 3 resource id, 4 temperature, 5 fan RPM.
/// Any failure disables it for the session and the caller falls back to alienfan-cli.
/// </summary>
public sealed class AwccSensors
{
    private const uint Invalid = 0xFFFFFFFF;
    private ManagementObject? _inst;
    private readonly List<int> _fanIds = new();
    private readonly List<int> _tempIds = new();
    private bool _initDone;
    private int _failures;

    public bool Disabled { get; private set; }

    private uint Call(int op, int arg)
    {
        using var p = _inst!.GetMethodParameters("Thermal_Information");
        p["arg2"] = (uint)(op | (arg << 8));
        using var r = _inst.InvokeMethod("Thermal_Information", p, null);
        return Convert.ToUInt32(r["argr"]);
    }

    private bool Init()
    {
        _initDone = true;
        try
        {
            using var cls = new ManagementClass(@"root\WMI", "AWCCWmiMethodFunction", null);
            foreach (ManagementObject o in cls.GetInstances()) { _inst = o; break; }
            if (_inst is null) return false;

            uint desc = Call(2, 0);
            int fans = (int)(desc & 0xFF), temps = (int)((desc >> 8) & 0xFF);
            if (fans == 0 && temps == 0) return false;
            for (int i = 0; i < fans + temps; i++)
            {
                int id = (int)(Call(3, i) & 0xFF);
                (i < fans ? _fanIds : _tempIds).Add(id);
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Snapshot in the same shape as the CLI parser, or null when WMI is unusable (use the CLI).</summary>
    public SensorSnapshot? Read()
    {
        if (Disabled) return null;
        if (!_initDone && !Init()) { Disabled = true; return null; }
        try
        {
            var s = new SensorSnapshot();
            foreach (int id in _tempIds)
            {
                uint t = Call(4, id);
                if (t == Invalid || t > 150) continue;
                string name = id switch { 0x01 => "CPU Internal Thermistor", 0x06 => "GPU Internal Thermistor", _ => $"Sensor 0x{id:X2}" };
                s.Temps.Add(new TempReading(name, (int)t));
            }
            for (int i = 0; i < _fanIds.Count; i++)
            {
                uint rpm = Call(5, _fanIds[i]);
                if (rpm == Invalid) continue;
                string type = _fanIds[i] switch { 0x32 => "CPU", 0x33 => "GPU", _ => "" };
                s.Fans.Add(new FanReading(i, type, (int)rpm));
            }
            _failures = 0;
            return s;
        }
        catch
        {
            if (++_failures >= 3) Disabled = true;
            return null;
        }
    }
}
