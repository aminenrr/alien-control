using System.Drawing;

namespace AlienControl.Services;

/// <summary>Tray profiles. They set the power mode and refresh rate only: never the GPU mode, never Ultimate.</summary>
public enum TrayProfile { Battery, Quiet, Balanced, Performance, Custom }

public static class Profiles
{
    public static string Label(TrayProfile p) => p.ToString();

    public static string Describe(TrayProfile p) => p switch
    {
        TrayProfile.Battery => "Battery Saver power mode, 60 Hz",
        TrayProfile.Quiet => "Quiet power mode, 60 Hz",
        TrayProfile.Balanced => "Balanced power mode, 240 Hz",
        TrayProfile.Performance => "Performance power mode, 240 Hz",
        _ => "Your own mix: nothing is changed"
    };

    /// <summary>Tray icon tint: amber / green / cyan / red / white.</summary>
    public static Color Tint(TrayProfile p) => p switch
    {
        TrayProfile.Battery => Color.FromArgb(255, 176, 32),
        TrayProfile.Quiet => Color.FromArgb(60, 255, 122),
        TrayProfile.Balanced => Color.FromArgb(0, 229, 255),
        TrayProfile.Performance => Color.FromArgb(255, 77, 94),
        _ => Color.White
    };

    /// <summary>Refresh rate a profile asks for, or null (Custom leaves it alone).</summary>
    public static int? RefreshHz(TrayProfile p) => p switch
    {
        TrayProfile.Battery or TrayProfile.Quiet => 60,
        TrayProfile.Balanced or TrayProfile.Performance => 240,
        _ => null
    };

    public static int? PowerIndex(TrayProfile p, Settings s) => p switch
    {
        TrayProfile.Battery => s.BatteryPowerIndex,
        TrayProfile.Quiet => s.QuietPowerIndex,
        TrayProfile.Balanced => s.BalancedPowerIndex,
        TrayProfile.Performance => s.PerformancePowerIndex,
        _ => null
    };
}
