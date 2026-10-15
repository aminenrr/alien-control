using System.Drawing;

namespace AlienControl.Services;

/// <summary>Tray profiles. They set the power mode, CPU boost and refresh rate only: never the GPU mode, never Ultimate.
/// v0.9.8: GMode is display-only (header chip + tray icon while G-Mode is on). It's never saved, never in the tray
/// menu, and never applied: leaving G-Mode brings the saved preset back.</summary>
public enum TrayProfile { Battery, Quiet, Balanced, Performance, Custom, GMode }

public static class Profiles
{
    public static string Label(TrayProfile p) => p == TrayProfile.GMode ? "G-Mode" : p.ToString();

    public static string Describe(TrayProfile p) => p switch
    {
        TrayProfile.Battery => "Battery Saver power mode, CPU boost Off, 60 Hz",
        TrayProfile.Quiet => "Quiet power mode, CPU boost Efficient, 240 Hz",
        TrayProfile.Balanced => "Balanced power mode, CPU boost On, 240 Hz",
        TrayProfile.Performance => "Performance power mode, CPU boost Aggressive, 240 Hz",
        TrayProfile.GMode => "G-Mode (F1): your preset comes back when you leave it",
        _ => "Your own mix: nothing is changed"
    };

    /// <summary>Tray icon tint: amber / green / cyan / red / white, violet for G-Mode.</summary>
    public static Color Tint(TrayProfile p) => p switch
    {
        TrayProfile.Battery => Color.FromArgb(255, 176, 32),
        TrayProfile.Quiet => Color.FromArgb(60, 255, 122),
        TrayProfile.Balanced => Color.FromArgb(0, 229, 255),
        TrayProfile.Performance => Color.FromArgb(255, 77, 106),   // #FF4D6A (v0.8.1 mockup)
        TrayProfile.GMode => Color.FromArgb(178, 102, 255),        // #B266FF (v0.9.8)
        _ => Color.White
    };

    /// <summary>Refresh rate a profile asks for, or null (Custom leaves it alone).</summary>
    public static int? RefreshHz(TrayProfile p) => p switch
    {
        TrayProfile.Battery => 60,                                                  // v0.8.5: only Battery drops to 60 Hz
        TrayProfile.Quiet or TrayProfile.Balanced or TrayProfile.Performance => 240,
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

    /// <summary>v0.8.9 (Amine): CPU boost per profile, as Windows PERFBOOSTMODE (0 Off, 1 On, 2 Aggressive,
    /// 3 Efficient). Set for both AC and battery. Custom leaves it alone.</summary>
    public static int? CpuBoost(TrayProfile p) => p switch
    {
        TrayProfile.Battery => 0,
        TrayProfile.Quiet => 3,
        TrayProfile.Balanced => 1,
        TrayProfile.Performance => 2,
        _ => null
    };
}
