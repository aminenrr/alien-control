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
    public int QuietPowerIndex { get; set; } = 4;
    public int BalancedPowerIndex { get; set; } = 1;
    public int PerformancePowerIndex { get; set; } = 5;

    /// <summary>setpower index -> raw value reported by getpower.</summary>
    public Dictionary<int, int> PowerRawByIndex { get; set; } = new()
    {
        [1] = 0xA0, [2] = 0xA1, [3] = 0xA5, [4] = 0xA3, [5] = 0xA4
    };

    public int MaxFanBoost { get; set; } = 100;

    // Lighting: device 0 = per-key keyboard (APIv5), device 1 = AW-ELC chassis (APIv4).
    public int KeyboardDevice { get; set; } = 0;
    public int KeyboardLightCount { get; set; } = 136;
    public int ChassisDevice { get; set; } = 1;
    public int[] ChassisLights { get; set; } = { 0, 2, 3, 4, 5 }; // Alienhead, KB zones (1 = power button, left alone)

    // Status keys. Any software color switches the keyboard out of firmware mode, which stops it
    // lighting these, so the app does it: normal color when off, IndicatorOnColor when on.
    public int GModeKey { get; set; } = 1;          // F1
    public int TouchpadKey { get; set; } = 12;      // F12 T-PAD
    public int SpeakerMuteKey { get; set; } = 16;
    public int MicMuteKey { get; set; } = 19;
    public int WinLockKey { get; set; } = 109;      // right Windows key (state not readable yet)
    public byte[] IndicatorOnColor { get; set; } = { 255, 255, 255 };

    /// <summary>Last static color picked; reapplied at app start. Null = leave firmware lighting alone.</summary>
    public byte[]? LastColor { get; set; }

    // G-key (F1 G-Mode key). AWCC used to handle it; the app now listens for it.
    // Virtual-key code; 0x80 = F17, the usual Dell G-key. Use "Detect" in the app to learn yours.
    public bool GKeyEnabled { get; set; } = true;
    public int GKeyVk { get; set; } = 0x80;
    public int GKeyScan { get; set; } = 0; // 0 = match any scan code

    public int PollSeconds { get; set; } = 2;

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
                // Older file had guessed power indexes; replace with verified defaults.
            }
        }
        catch { /* fall back to defaults */ }
        var d = new Settings();
        d.Save();
        return d;
    }

    private const int CurrentVersion = 2;
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
