using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AlienControl.Services;

/// <summary>User settings stored in %AppData%\AlienControl\settings.json.</summary>
public sealed class Settings
{
    // setpower takes an index; getpower reports the raw BIOS value.
    // Verified on the m15 R7 (System ID 83886594):
    //   1 = a0 Balanced, 2 = a1 Balanced-Performance, 3 = a5 Battery Saver, 4 = a3 Quiet, 5 = a4 Performance
    public int BatteryPowerIndex { get; set; } = 3;     // a5 Low Power / Battery Saver
    public int QuietPowerIndex { get; set; } = 4;
    public int BalancedPowerIndex { get; set; } = 1;
    public int PerformancePowerIndex { get; set; } = 2; // a1; index 5 (a4) is the G-Mode table

    /// <summary>setpower index -> raw value reported by getpower.</summary>
    public Dictionary<int, int> PowerRawByIndex { get; set; } = new()
    {
        [1] = 0xA0, [2] = 0xA1, [3] = 0xA5, [4] = 0xA3, [5] = 0xA4
    };

    public int MaxFanBoost { get; set; } = 100;
    public int FanMaxRpm { get; set; } = 5000; // alienfan-cli maxrpm on the m15 R7

    // Lighting: device 0 = per-key keyboard (APIv5), device 1 = AW-ELC chassis (APIv4).
    public int KeyboardDevice { get; set; } = 0;
    public int KeyboardLightCount { get; set; } = 136;
    public int ChassisDevice { get; set; } = 1;
    // Mapped on the m15 R7 (2026-10-14): 0 + 1 = rear ring (painted as one light), 3 = lid Alienhead.
    // 2 = power button: never sent, the firmware keeps blue on AC / amber on battery. 4..15 = nothing visible.
    public int[] ChassisLights { get; set; } = { 0, 1, 3 };
    public const int PowerButtonLight = 2;

    // Status keys. Any software color switches the keyboard out of firmware mode, which stops it
    // lighting these, so the app does it: normal color when off, IndicatorOnColor when on.
    public int GModeKey { get; set; } = 1;          // F1
    public int TouchpadKey { get; set; } = 12;      // F12 T-PAD
    public int SpeakerMuteKey { get; set; } = 16;
    public int MicMuteKey { get; set; } = 19;
    public int WinLockKey { get; set; } = 109;      // right Windows key (state not readable yet)
    /// <summary>Hardware brightness 0..255, re-sent after every keyboard frame (a frame resets it).</summary>
    public int Brightness { get; set; } = 204;

    public byte[] IndicatorOnColor { get; set; } = { 255, 255, 255 };

    /// <summary>Last static color picked; reapplied at app start. Null = leave firmware lighting alone.</summary>
    public byte[]? LastColor { get; set; }

    /// <summary>APIv5 effect type: 0 Static, 2 Breathing, 3 Wave, 4 Dual wave, 8 Pulse, 9 Mixed pulse, 10 Night Rider, 11 Laser.</summary>
    public int LastEffect { get; set; } = 0;

    // G-key (F1 G-Mode key). AWCC used to handle it; the app now listens for it. Always on since v0.8.
    // Virtual-key code; 0x80 = F17, the usual Dell G-key. Use "Detect key" (gear menu) to learn yours.
    public int GKeyVk { get; set; } = 0x80;
    public int GKeyScan { get; set; } = 0; // 0 = match any scan code

    public int PollSeconds { get; set; } = 2;

    /// <summary>UI theme: "Legend" (default) or "Fluent".</summary>
    public string Theme { get; set; } = "Legend";

    /// <summary>Tray profile: Battery, Quiet, Balanced, Performance or Custom.</summary>
    public string Profile { get; set; } = "Custom";

    /// <summary>Charge mode in use before Express charging was turned on (restored when it's turned off).</summary>
    public string? ChargeModeBeforeExpress { get; set; }

    /// <summary>v0.8.2: true = skip every UI animation (instant, like v0.8.1). Default off.</summary>
    public bool ReduceMotion { get; set; } = false;

    /// <summary>v0.8.5: learned "rest of the laptop" power (W) for the AC total estimate. No migration needed.</summary>
    public double RestOfSystemW { get; set; } = 15;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlienControl", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (s is not null && s.SettingsVersion >= CurrentVersion) return s;
                if (s is not null && s.SettingsVersion is >= 2 and < CurrentVersion)
                {
                    // v2 sent the power button (ID 2) and missed half the ring (ID 1).
                    if (s.SettingsVersion == 2) s.ChassisLights = new[] { 0, 1, 3 };
                    // v3 mapped Performance to index 5 (a4), which is the G-Mode table.
                    if (s.PerformancePowerIndex == 5) s.PerformancePowerIndex = 2;
                    // v5 (app v0.8): the G-key is always on (GKeyEnabled and GpuOptimized are dropped and
                    // ignored when read), Battery mode = index 3, theme defaults to Legend.
                    if (s.BatteryPowerIndex <= 0) s.BatteryPowerIndex = 3;
                    if (s.Theme is not ("Legend" or "Fluent")) s.Theme = "Legend";
                    // v6 (app v0.8.2): adds ReduceMotion; older files have no key, so it reads as false (animations on).
                    if (s.SettingsVersion < 6) s.ReduceMotion = false;
                    s.SettingsVersion = CurrentVersion;
                    s.Save();
                    return s;
                }
                // Older file had guessed power indexes; replace with verified defaults.
            }
        }
        catch { /* fall back to defaults */ }
        var d = new Settings();
        d.Save();
        return d;
    }

    private const int CurrentVersion = 6;
    public int SettingsVersion { get; set; } = CurrentVersion;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}
