using System;

namespace AlienControl.Overlay;

/// <summary>beta1.3-ov: what one overlay row can show. Each level is a fixed set, copied from MangoHud's presets().</summary>
[Flags]
public enum Item
{
    None = 0,
    Fps = 1 << 0, FrameTime = 1 << 1, FrameGraph = 1 << 2,
    Cpu = 1 << 3, CpuTemp = 1 << 4, CpuPower = 1 << 5, CpuMhz = 1 << 6,
    Gpu = 1 << 7, GpuTemp = 1 << 8, GpuPower = 1 << 9, GpuClocks = 1 << 10,
    Ram = 1 << 11, Vram = 1 << 12, Battery = 1 << 13, Engine = 1 << 14,
    CoreLoads = 1 << 15, Fans = 1 << 16, Refresh = 1 << 17, FrameStats = 1 << 18,
    /// <summary>Latency and real / generated fps: only with the "Latency and frame gen" toggle, levels 2 to 4.</summary>
    FrameGen = 1 << 19, Latency = 1 << 20,
    /// <summary>ov.5 (Amine): 1 % low fps (levels 1, 3, 4) and 0.1 % low fps (level 4). From the frames already read, no new source.</summary>
    Low1 = 1 << 21, Low01 = 1 << 22,
    /// <summary>ov.6: flip mode (independent vs composed), level 4 with the latency toggle only (needs display tracking).</summary>
    PresentMode = 1 << 23,
}

/// <summary>What the sensor side must read for a level. Only these run; level 1 reads frames only.</summary>
[Flags]
public enum Need
{
    None = 0,
    Frames = 1 << 0,
    Acpi = 1 << 1,        // temps + fans (WMI / alienfan-cli, the window's 2 s sensor poll)
    Usage = 1 << 2,       // CPU %, GPU %, RAM, VRAM (the same poll)
    Power = 1 << 3,       // CPU W (RAPL), GPU W (NVML), battery rate (the window's 1 s power timer)
    Clocks = 1 << 4,      // GPU core / mem clock (NVML), CPU MHz
    Cores = 1 << 5,       // per core load
    Battery = 1 << 6,     // battery % / AC (GetSystemPowerStatus, free)
    Refresh = 1 << 7,     // display refresh rate (EnumDisplaySettings, every 5 s)
}

public static class OverlayLevels
{
    public const int Max = 4;

    /// <summary>MangoHud preset 1: fps_only.</summary>
    public const Item L1 = Item.Fps | Item.Low1;   // ov.5: + 1 % low (Amine)

    /// <summary>MangoHud preset 2 (horizontal): fps, frame_timing, cpu/gpu load, ram, vram, battery, cpu/gpu power.</summary>
    public const Item L2 = Item.Fps | Item.FrameTime | Item.FrameGraph | Item.Cpu | Item.Gpu | Item.Ram | Item.Vram
                         | Item.Battery | Item.CpuPower | Item.GpuPower | Item.Refresh;   // ov.5: + RR after battery (Amine)

    /// <summary>MangoHud preset 3: the default vertical HUD + temps, power, cpu mhz, gpu core and mem clock, ram, vram, battery.</summary>
    public const Item L3 = Item.Fps | Item.FrameTime | Item.FrameGraph | Item.Engine
                         | Item.Cpu | Item.CpuTemp | Item.CpuPower | Item.CpuMhz
                         | Item.Gpu | Item.GpuTemp | Item.GpuPower | Item.GpuClocks
                         | Item.Ram | Item.Vram | Item.Battery | Item.Low1   // ov.5: + 1 % low (Amine)
                         | Item.Refresh;   // ov.9: + DISPLAY under BATT, like level 4 (Amine)

    /// <summary>MangoHud preset 4: level 3 + per core load and clock (P and E cores), fans, refresh rate, detailed frame timing.
    /// MangoHud's network row isn't copied (no source that's cheap enough on Windows).</summary>
    public const Item L4 = L3 | Item.CoreLoads | Item.Fans | Item.FrameStats | Item.Low01;   // ov.5: + 0.1 % low

    public static Item Items(int level) => level switch { 1 => L1, 2 => L2, 3 => L3, 4 => L4, _ => Item.None };

    /// <summary>Items with the latency and frame gen toggle: it adds FrameGen + Latency on levels 2 to 4, never on level 1.</summary>
    public static Item Items(int level, bool extended) =>
        extended && level >= 2 ? Items(level) | Item.FrameGen | Item.Latency | (level == 4 ? Item.PresentMode : Item.None) : Items(level);

    /// <summary>ov.9: the Show on overlay toggles. Refresh or battery off drops it from every level (and it isn't read).</summary>
    public static Item Hidden(bool showRefresh, bool showBattery) =>
        (showRefresh ? Item.None : Item.Refresh) | (showBattery ? Item.None : Item.Battery);

    public static Item Items(int level, bool extended, Item hidden) => Items(level, extended) & ~hidden;

    public static Need Needs(int level, Item hidden) => NeedsOf(Items(level) & ~hidden);

    /// <summary>Does PresentMon need the extended tracking? Only when the toggle is on and the level shows it.</summary>
    public static bool WantsExtended(int level, bool toggle) => toggle && level >= 2;

    public static bool Horizontal(int level) => level == 2;

    public static Need Needs(int level) => NeedsOf(Items(level));

    public static Need NeedsOf(Item it)
    {
        if (it == Item.None) return Need.None;
        var n = Need.Frames;
        if ((it & (Item.CpuTemp | Item.GpuTemp | Item.Fans)) != 0) n |= Need.Acpi;
        if ((it & (Item.Cpu | Item.Gpu | Item.Ram | Item.Vram)) != 0) n |= Need.Usage;
        if ((it & (Item.CpuPower | Item.GpuPower)) != 0) n |= Need.Power;
        if ((it & (Item.CpuMhz | Item.GpuClocks)) != 0) n |= Need.Clocks;
        if ((it & Item.CoreLoads) != 0) n |= Need.Cores;
        if ((it & Item.Battery) != 0) n |= Need.Battery;
        if ((it & Item.Refresh) != 0) n |= Need.Refresh;
        return n;
    }
}

/// <summary>The latest sensor values the overlay draws. Immutable, swapped whole (no locks on the overlay thread).</summary>
public sealed record Sensors
{
    public double? CpuLoad { get; init; }
    public int? CpuTemp { get; init; }
    public int? CpuW { get; init; }
    public int? CpuMhz { get; init; }
    public double? GpuLoad { get; init; }
    public int? GpuTemp { get; init; }
    public int? GpuW { get; init; }
    public int? GpuCoreMhz { get; init; }
    public int? GpuMemMhz { get; init; }
    public bool GpuOff { get; init; }
    public double? RamGiB { get; init; }
    public double? VramGiB { get; init; }
    public int? BatteryPct { get; init; }
    public bool OnAc { get; init; }
    public int? BatteryW { get; init; }
    public int? CpuFanRpm { get; init; }
    public int? GpuFanRpm { get; init; }
    public int? RefreshHz { get; init; }
    /// <summary>One row per physical core (P / E / C), thread loads and the core clock.</summary>
    public AlienControl.Services.CpuCores.CoreReading[]? Cores { get; init; }
}
