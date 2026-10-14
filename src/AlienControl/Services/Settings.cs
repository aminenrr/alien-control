using System;
using System.IO;
using System.Text.Json;

namespace AlienControl.Services;

/// <summary>User settings stored in %AppData%\AlienControl\settings.json.</summary>
public sealed class Settings
{
    // setpower indexes differ per machine; run alienfan-cli without arguments to see the list
    // and adjust these. Defaults are guesses until verified on the m15 R7.
    public int QuietPowerIndex { get; set; } = 1;
    public int BalancedPowerIndex { get; set; } = 2;
    public int PerformancePowerIndex { get; set; } = 3;

    public int MaxFanBoost { get; set; } = 100;
    public int LightDevice { get; set; } = 0;
    public int SpectrumEffectType { get; set; } = 8; // APIv8 "Spectrum"; APIv5 wave is 3
    public int PollSeconds { get; set; } = 2;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlienControl", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* fall back to defaults */ }
        var s = new Settings();
        s.Save();
        return s;
    }

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
