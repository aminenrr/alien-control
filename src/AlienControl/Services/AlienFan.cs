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

    private readonly AwccSensors _wmi = new();

    public bool IsAvailable => _cli.IsAvailable;

    /// <summary>Reads temperatures and fan RPMs: WMI first (no process spawn), alienfan-cli as fallback.</summary>
    public async Task<SensorSnapshot> ReadSensorsAsync()
    {
        // 2.0.2 (stability audit 4a): no WMI read queued behind a timed-out CLI (it would park a pool thread per poll)
        if (HardwareController.Stuck) return new SensorSnapshot { Error = CliRunner.StuckMessage };
        var w = await Task.Run(() => HardwareController.Run(_wmi.Read));
        if (w is not null) return w;

        var r = await _cli.RunAsync("temp rpm");
        if (!r.Ok && r.Output.Length == 0) return new SensorSnapshot { Error = r.Error };
        return ParseSensors(r.Output);
    }

    /// <summary>Parses "temp rpm" output (1.3.0-beta.1: static for the tests).</summary>
    internal static SensorSnapshot ParseSensors(string output)
    {
        var snap = new SensorSnapshot();
        foreach (var raw in output.Split('\n'))
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
        return ParsePowerRaw(r.Output);
    }

    internal static int? ParsePowerRaw(string output)
    {
        var m = Regex.Match(output, @"Power mode:.*\(([0-9a-fA-F]+)\)");
        return m.Success ? int.Parse(m.Groups[1].Value, NumberStyles.HexNumber) : null;
    }

    public Task<CliResult> SetPowerAsync(int index) => _cli.RunAsync($"setpower={index}");

    public async Task<bool?> GetGModeAsync()
    {
        var r = await _cli.RunAsync("gmode");
        return ParseGMode(r.Output);
    }

    internal static bool? ParseGMode(string output)
    {
        var m = Regex.Match(output, @"G-mode\s*(is\s*)?(On|Off)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[2].Value.Equals("On", System.StringComparison.OrdinalIgnoreCase) : null;
    }

    public Task<CliResult> SetGModeAsync(bool on) => _cli.RunAsync($"setgmode={(on ? 1 : 0)}");

    /// <summary>0..4 = disabled, enabled, aggressive, efficient, efficient aggressive (AC, battery).</summary>
    public Task<CliResult> SetCpuBoostAsync(int ac, int dc) => _cli.RunAsync($"setperf={ac},{dc}");

    /// <summary>Boost in percent above BIOS level, one value per fan.</summary>
    public Task<CliResult> SetFanBoostAsync(IReadOnlyList<int> perFan) =>
        _cli.RunAsync("setfans=" + string.Join(",", perFan));
}

public sealed class SensorSnapshot
{
    public List<TempReading> Temps { get; } = new();
    public List<FanReading> Fans { get; } = new();
    public string? Error { get; set; }
}

public sealed record TempReading(string Name, int Celsius);
public sealed record FanReading(int Id, string Type, int Rpm);
