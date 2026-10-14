using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AlienControl.Services;

/// <summary>
/// Thin wrapper over alienfan-cli (fans, power, G-Mode, CPU boost, temps). setgpu is not in v9.6 and is not used.
/// Command reference: https://github.com/T-Troll/alienfx-tools/blob/master/Doc/alienfan-cli.md
/// </summary>
public sealed class AlienFan
{
    private readonly CliRunner _cli = new("alienfan-cli.exe");

    public bool IsAvailable => _cli.IsAvailable;

    /// <summary>Reads temperatures and fan RPMs in a single CLI call (one ACPI probe).</summary>
    public async Task<SensorSnapshot> ReadSensorsAsync()
    {
        var r = await _cli.RunAsync("temp rpm");
        var snap = new SensorSnapshot();
        if (!r.Ok && r.Output.Length == 0) { snap.Error = r.Error; return snap; }

        foreach (var raw in r.Output.Split('\n'))
        {
            var line = raw.Trim();
            // Fan lines: "Fan CPU 0 RPM: 3100" / "Fan GPU 1 RPM: 2950" / "Fan 2 RPM: 0"
            var fan = Regex.Match(line, @"^Fan\s*(CPU|GPU)?\s*(\d+)\s+RPM:\s*(-?\d+)$", RegexOptions.IgnoreCase);
            if (fan.Success)
            {
                snap.Fans.Add(new FanReading(int.Parse(fan.Groups[2].Value, CultureInfo.InvariantCulture),
                    fan.Groups[1].Value.ToUpperInvariant(), int.Parse(fan.Groups[3].Value, CultureInfo.InvariantCulture)));
                continue;
            }
            // Sensor lines: "<sensor name>: 62"
            var t = Regex.Match(line, @"^(.+?):\s*(-?\d+)$");
            if (t.Success && !line.StartsWith("Supported hardware"))
                snap.Temps.Add(new TempReading(t.Groups[1].Value, int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture)));
        }
        return snap;
    }

    /// <summary>Raw BIOS power value, e.g. 0xA0 from "Power mode: Level 160 (a0)". Null if unreadable.</summary>
    public async Task<int?> GetPowerRawAsync()
    {
        var r = await _cli.RunAsync("getpower");
        var m = Regex.Match(r.Output, @"Power mode:.*\(([0-9a-fA-F]+)\)");
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Value, NumberStyles.HexNumber);
    }

    public Task<CliResult> SetPowerAsync(int index) => _cli.RunAsync($"setpower={index}");

    public async Task<bool?> GetGModeAsync()
    {
        var r = await _cli.RunAsync("gmode");
        var m = Regex.Match(r.Output, @"G-mode\s*(is\s*)?(On|Off)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return m.Groups[2].Value.Equals("On", System.StringComparison.OrdinalIgnoreCase);
    }

    public Task<CliResult> SetGModeAsync(bool on) => _cli.RunAsync($"setgmode={(on ? 1 : 0)}");

    /// <summary>0..4 = disabled, enabled, aggressive, efficient, efficient aggressive (AC, battery).</summary>
    public Task<CliResult> SetCpuBoostAsync(int ac, int dc) => _cli.RunAsync($"setperf={ac},{dc}");

    /// <summary>Boost in percent above BIOS level, one value per fan.</summary>
    public Task<CliResult> SetFanBoostAsync(IReadOnlyList<int> perFan) =>
        _cli.RunAsync("setfans=" + string.Join(",", perFan));

    /// <summary>Raw output of the CLI started with no arguments (hardware summary + help).</summary>
    public Task<CliResult> ProbeAsync() => _cli.RunAsync("");
}

public sealed class SensorSnapshot
{
    public List<TempReading> Temps { get; } = new();
    public List<FanReading> Fans { get; } = new();
    public string? Error { get; set; }
}

public sealed record TempReading(string Name, int Celsius);
public sealed record FanReading(int Id, string Type, int Rpm);
