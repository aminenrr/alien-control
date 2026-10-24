using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <summary>1.3.1: chassis lights per zone (docs/HARDWARE.md): rear "tron" ring = 0 + 1, lid Alienhead = 3.</summary>
    public static readonly int[] RingLights = { 0, 1 };
    public const int AlienheadLight = 3;

    // 1.3.1 Lighting > Off menu: each light can be turned off on its own (any mix). Off = the keyboard's hardware
    // brightness goes to 0 with its colors kept (so F7 still works); the chassis lights get black.
    public bool OffKeyboard { get; set; }
    public bool OffRing { get; set; }
    public bool OffAlienhead { get; set; }
    /// <summary>1.3.1 Ctrl + Alt + F: lights at half of Brightness (keyboard, rear ring, Alienhead).</summary>
    public bool LightsHalf { get; set; }

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

    /// <summary>v0.9.3: when the app last wrote the BIOS graphics switch (UTC). Written after the last boot = not applied yet.
    /// Null after a cancel (BIOS back to what's running).</summary>
    public DateTime? GpuSwitchSetUtc { get; set; }
    /// <summary>1.3.0-beta.1: Windows BootId when the switch was written (Fast Startup safe).</summary>
    public int? GpuSwitchSetBootId { get; set; }

    // v0.9.7 GPU overclock (G-Mode on AC only). OcCore / OcMem = AWCC-style MHz (0..300), the last kept values.
    // OcOn = kept with "Keep" and reapplied when G-Mode is on and plugged in. OcPending = a test was written but not
    // kept yet: still true at launch means the app or driver died mid-test, so the offsets go back to 0.
    public int OcCore { get; set; } = 200;
    public int OcMem { get; set; } = 200;
    public bool OcOn { get; set; } = false;
    public bool OcPending { get; set; } = false;
    /// <summary>1.3.4 (Amine, 2026-10-21): header toggle in the GPU OC section. On = the kept offsets also apply in the
    /// Performance preset (on AC), not just G-Mode. Off (default) = G-Mode only, as before.</summary>
    public bool OcInPerformance { get; set; } = false;
    /// <summary>2.0.0-beta1.2 (Amine): G-MODE and + PERF are separate switches. On (default, as before) = the kept
    /// offsets apply in G-Mode (on AC). Both off = the overclock never runs, even with offsets kept.</summary>
    public bool OcInGMode { get; set; } = true;

    /// <summary>Tray profile: Battery, Quiet, Balanced, Performance or Custom.</summary>
    public string Profile { get; set; } = "Custom";

    /// <summary>Charge mode in use before Express charging was turned on (restored when it's turned off).</summary>
    public string? ChargeModeBeforeExpress { get; set; }

    /// <summary>v0.8.2: true = skip every UI animation (instant, like v0.8.1). Default off.</summary>
    public bool ReduceMotion { get; set; } = false;

    /// <summary>v0.9.10: F2 (Macro A, media mode) cycles Quiet > Balanced > Performance and tints the F2 key. Default on.</summary>
    public bool F2Presets { get; set; } = true;

    /// <summary>v0.9.10: F2's light id on the keyboard (guessed 2, verify on the laptop).</summary>
    public int F2Key { get; set; } = 2;

    /// <summary>v0.9.16: F3 (Macro B, media mode) opens / hides the window, like a handheld's quick menu. Default on.</summary>
    public bool F3Opens { get; set; } = true;
    /// <summary>2.0: F4 (Macro C, media mode) is reserved for the overlay (beta 2). Default on. In beta 1 it only shows a hint.</summary>
    public bool F4Overlay { get; set; } = true;

    // beta1.3-ov: the in-game overlay (MangoHud style). Level 0 = off (nothing runs), 1 to 4 = SteamOS levels.
    public int OverlayLevel { get; set; } = 0;
    /// <summary>F4 brings back this level after off.</summary>
    public int OverlayLastLevel { get; set; } = 3;
    /// <summary>0 top left (SteamOS), 1 top centre, 2 top right, 3 bottom left, 4 bottom centre, 5 bottom right.</summary>
    public int OverlayPosition { get; set; } = 0;
    /// <summary>Size in % of MangoHud's 24 px font (100 = SteamOS size).</summary>
    public int OverlaySize { get; set; } = 100;
    /// <summary>Background opacity in % (MangoHud default 50).</summary>
    public int OverlayBg { get; set; } = 50;
    /// <summary>Update interval in ms: 250, 500 (MangoHud default fps_sampling_period) or 1000.</summary>
    public int OverlayUpdateMs { get; set; } = 500;
    /// <summary>ov.7: LIVE update: the frametime graph redraws ~30 times a second. Off by default (costs a little CPU in this app).</summary>
    public bool OverlaySmoothGraph { get; set; } = false;
    /// <summary>ov.9 Show on overlay: refresh rate / battery on every level that has them. On by default (as before).</summary>
    public bool OverlayShowRefresh { get; set; } = true;
    public bool OverlayShowBattery { get; set; } = true;
    /// <summary>Nudge from the chosen spot, in % of the screen: X -50 (left) .. 50 (right), Y -50 (up) .. 50 (down).
    /// The overlay always stays on screen.</summary>
    public int OverlayOffsetX { get; set; } = 0;
    public int OverlayOffsetY { get; set; } = 0;
    /// <summary>Latency and frame gen (real / generated fps). Off by default: PresentMon collects none of it while off.</summary>
    public bool OverlayLatencyFg { get; set; } = false;

    /// <summary>ov.11 FPS cap (NVIDIA driver profile): the last game the overlay saw (exe and a readable name), the scope
    /// (false = this game, true = all games) and the last custom value. The cap and V-Sync themselves live in the
    /// driver's profile, never here, so the NVIDIA app and this card always agree.</summary>
    public string? FpsCapGame { get; set; }
    public string? FpsCapTitle { get; set; }
    public bool FpsCapAll { get; set; } = false;
    public int FpsCapCustom { get; set; } = 165;

    /// <summary>ov.3 (Amine): each level remembers its own look: position, size, background, move X / Y.
    /// Index 0 = level 1 ... 3 = level 4. The single OverlayPosition / Size / Bg / OffsetX / OffsetY above are only
    /// the seed for settings files from ov.1 / ov.2.</summary>
    public List<OverlayLook>? OverlayLooks { get; set; }

    public OverlayLook LookFor(int level)
    {
        EnsureLooks();
        return OverlayLooks![Math.Clamp(level, 1, 4) - 1];
    }

    private void EnsureLooks()
    {
        if (OverlayLooks is { Count: 4 } && !OverlayLooks.Contains(null!)) return;
        OverlayLooks = new List<OverlayLook>();
        for (int i = 0; i < 4; i++)
            OverlayLooks.Add(new OverlayLook { Position = OverlayPosition, Size = OverlaySize, Bg = OverlayBg, X = OverlayOffsetX, Y = OverlayOffsetY });
    }
    /// <summary>v1.1: off = no taskbar button, the app only lives in the tray (minimize hides to the tray).</summary>
    public bool ShowInTaskbar { get; set; } = true;

    /// <summary>v0.9.11: unplug -> Battery, plug in -> AcPreset (Amine: on by default).</summary>
    public bool AutoPreset { get; set; } = true;
    /// <summary>v0.9.11: preset to bring back on plug-in (Quiet / Balanced / Performance).</summary>
    public string AcPreset { get; set; } = "Balanced";
    /// <summary>v0.9.11: top-right toast for F1, F2 and plug / unplug.</summary>
    public bool Toasts { get; set; } = true;
    /// <summary>v0.9.11: soft sound with those events (own toggle).</summary>
    public bool ToastSound { get; set; } = true;
    /// <summary>1.3.0-beta.2: "Soft" (default, also for upgrades: the key is new) or "Classic" (the v1.2 chimes).</summary>
    public string ChimePack { get; set; } = "Soft";
    /// <summary>1.3.0-beta.2: "Pill" (top-center capsule, default, also for upgrades) or "Classic" (the v1.2 top-right card).</summary>
    public string ToastStyle { get; set; } = "Pill";

    /// <summary>v0.8.5: learned "rest of the laptop" power (W) for the AC total estimate. No migration needed.</summary>
    public double RestOfSystemW { get; set; } = 15;

    // 1.3.0-beta.1 keyboard backlight timeout (Part 2): minutes without input before the keys fade (0 = off).
    public int KbdTimeoutMin { get; set; } = 0;
    public bool KbdTimeoutBatteryOnly { get; set; } = false;
    public bool KbdTimeoutPauseFullscreen { get; set; } = true;

    // 1.3.0 animations: the beta's NormalWindow / NewSlideEngine toggles were removed after the A/B
    // (old settings.json keys are ignored on load).
    /// <summary>true = log frame gaps of every slide to crash.log.</summary>
    public bool FrameLog { get; set; } = false;

    internal static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlienControl", "settings.json");

    public static Settings Load()
    {
        // 1.3.0-beta.1 (review #8): a torn settings.json falls back to settings.json.bak instead of defaults
        // (defaults would also forget OcPending, the flag that undoes a crashed OC test).
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var s = Parse(File.ReadAllText(path));
                if (s is null) continue;
                bool migrated = s.SettingsVersion < CurrentVersion;
                s.Clamp();
                if (migrated || path != FilePath) s.Save();
                return s;
            }
            catch (Exception ex) { Log.Warn($"Settings.Load {Path.GetFileName(path)}", ex); }
        }
        var d = Defaults();
        d.Save();
        return d;
    }

    /// <summary>2.0.0-beta1.3 (services review #2): what Load falls back to when there is no readable settings.json or
    /// .bak (first start, Reset settings, or both files torn). Nothing says the driver is at stock then, so it is treated
    /// like a test that wasn't kept: OcPending makes the next start write +0 once.</summary>
    internal static Settings Defaults() => new() { OcPending = true, LoadedDefaults = true };

    /// <summary>2.0.0-beta1.3: true when this instance came from Defaults() (not saved), so the startup +0 is silent.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool LoadedDefaults { get; private set; }

    /// <summary>Reads + migrates one file. Null when it's unreadable or too old (guessed power indexes).</summary>
    internal static Settings? Parse(string json)
    {
        var s = JsonSerializer.Deserialize<Settings>(json);
        if (s is null) return null;
        if (s.SettingsVersion >= CurrentVersion) return s;
        if (s.SettingsVersion is < 2) return null;   // older file had guessed power indexes: verified defaults instead
        // v2 sent the power button (ID 2) and missed half the ring (ID 1).
        if (s.SettingsVersion == 2) s.ChassisLights = new[] { 0, 1, 3 };
        // v3 mapped Performance to index 5 (a4), which is the G-Mode table.
        if (s.PerformancePowerIndex == 5) s.PerformancePowerIndex = 2;
        // v5 (app v0.8): the G-key is always on, Battery mode = index 3, theme defaults to Legend.
        if (s.BatteryPowerIndex <= 0) s.BatteryPowerIndex = 3;
        // v6 (app v0.8.2): adds ReduceMotion; older files have no key, so it reads as false (animations on).
        if (s.SettingsVersion < 6) s.ReduceMotion = false;
        // v7 (app 2.0): the "Theme" key is gone (Legend only, Fluent removed). Old files still carry it; System.Text.Json
        // skips unknown keys, and the save after this migration drops it. Nothing else changes.
        s.SettingsVersion = CurrentVersion;
        return s;
    }

    /// <summary>1.3.0-beta.1 (review #7): settings.json is user-writable, so every value that reaches the hardware is
    /// clamped to the known-good range for the m15 R7 (power indexes 1..5, raw a0..a5, 136 keys, 16 chassis lights,
    /// never the power-button light).</summary>
    internal void Clamp()
    {
        static int C(int v, int lo, int hi, int def) => v < lo || v > hi ? def : v;
        var d = new Settings();
        BatteryPowerIndex = C(BatteryPowerIndex, 1, 5, d.BatteryPowerIndex);
        QuietPowerIndex = C(QuietPowerIndex, 1, 5, d.QuietPowerIndex);
        BalancedPowerIndex = C(BalancedPowerIndex, 1, 5, d.BalancedPowerIndex);
        PerformancePowerIndex = C(PerformancePowerIndex, 1, 4, d.PerformancePowerIndex);   // 5 = G-Mode table
        if (PowerRawByIndex is null || PowerRawByIndex.Count != 5 || !PowerRawByIndex.All(kv => kv.Key is >= 1 and <= 5 && kv.Value is >= 0xA0 and <= 0xA5))
            PowerRawByIndex = d.PowerRawByIndex;
        MaxFanBoost = C(MaxFanBoost, 0, 100, d.MaxFanBoost);
        FanMaxRpm = C(FanMaxRpm, 1000, 8000, d.FanMaxRpm);
        KeyboardDevice = C(KeyboardDevice, 0, 3, d.KeyboardDevice);
        // beta1.3-ov overlay
        OverlayLevel = C(OverlayLevel, 0, 4, d.OverlayLevel);
        OverlayLastLevel = C(OverlayLastLevel, 1, 4, d.OverlayLastLevel);
        OverlayPosition = C(OverlayPosition, 0, 5, d.OverlayPosition);
        OverlaySize = C(OverlaySize, 50, 150, d.OverlaySize);
        OverlayBg = C(OverlayBg, 0, 100, d.OverlayBg);
        if (OverlayUpdateMs is not (250 or 500 or 1000)) OverlayUpdateMs = d.OverlayUpdateMs;
        OverlayOffsetX = C(OverlayOffsetX, -50, 50, 0);
        OverlayOffsetY = C(OverlayOffsetY, -50, 50, 0);
        EnsureLooks();
        foreach (var l in OverlayLooks!)
        {
            l.Position = C(l.Position, 0, 5, 0);
            l.Size = C(l.Size, 50, 150, 100);
            l.Bg = C(l.Bg, 0, 100, 50);
            l.X = C(l.X, -50, 50, 0);
            l.Y = C(l.Y, -50, 50, 0);
        }
        FpsCapCustom = C(FpsCapCustom, NvDrs.MinFps, NvDrs.MaxFps, d.FpsCapCustom);
        if (FpsCapGame is { Length: > 260 }) FpsCapGame = null;
        KeyboardLightCount = C(KeyboardLightCount, 1, 136, d.KeyboardLightCount);
        ChassisDevice = C(ChassisDevice, 0, 3, d.ChassisDevice);
        ChassisLights = (ChassisLights ?? d.ChassisLights).Where(id => id is >= 0 and <= 15 && id != PowerButtonLight).Distinct().ToArray();
        GModeKey = C(GModeKey, 0, 135, d.GModeKey);
        TouchpadKey = C(TouchpadKey, 0, 135, d.TouchpadKey);
        SpeakerMuteKey = C(SpeakerMuteKey, 0, 135, d.SpeakerMuteKey);
        MicMuteKey = C(MicMuteKey, 0, 135, d.MicMuteKey);
        WinLockKey = C(WinLockKey, 0, 135, d.WinLockKey);
        F2Key = C(F2Key, 0, 135, d.F2Key);
        Brightness = C(Brightness, 0, 255, d.Brightness);
        if (IndicatorOnColor is not { Length: 3 }) IndicatorOnColor = d.IndicatorOnColor;
        if (LastColor is not null && LastColor.Length != 3) LastColor = null;
        // 1.3.1: 100 / 101 (the app's Spectrum / Rainbow wave) were missing here, so they fell back to Static on restart.
        if (LastEffect is not (0 or 2 or 3 or 4 or 8 or 9 or 10 or 11 or 100 or 101)) LastEffect = 0;
        // 1.3.1: 1.3.0's Off painted everything black and saved that as the color. Now it's three off switches and the
        // color is kept, so a black color becomes "all three off" with the default cyan to come back to.
        if (LastColor is { Length: 3 } lc && lc.All(v => v == 0))
        {
            OffKeyboard = OffRing = OffAlienhead = true;
            LastColor = new byte[] { 0, 229, 255 };
        }
        GKeyVk = C(GKeyVk, 1, 254, d.GKeyVk);
        GKeyScan = C(GKeyScan, 0, 0x1FF, 0);
        PollSeconds = C(PollSeconds, 1, 10, d.PollSeconds);
        OcCore = C(OcCore, 0, GpuOc.MaxUi, 0);
        OcMem = C(OcMem, 0, GpuOc.MaxUi, 0);
        if (Profile is not ("Battery" or "Quiet" or "Balanced" or "Performance" or "Custom")) Profile = "Custom";
        if (AcPreset is not ("Quiet" or "Balanced" or "Performance")) AcPreset = "Balanced";
        if (ChimePack is not ("Soft" or "Classic")) ChimePack = "Soft";
        if (ToastStyle is not ("Pill" or "Classic")) ToastStyle = "Pill";
        KbdTimeoutMin = KbdTimeoutMin is 0 or 1 or 2 or 5 or 10 ? KbdTimeoutMin : 0;
        if (double.IsNaN(RestOfSystemW) || RestOfSystemW is < 0 or > 100) RestOfSystemW = d.RestOfSystemW;
    }

    /// <summary>v0.9.15: removes settings.json (next Load writes defaults).</summary>
    public static void Delete()
    {
        foreach (var f in new[] { FilePath, FilePath + ".bak", FilePath + ".tmp" })
            try { if (File.Exists(f)) File.Delete(f); } catch (Exception ex) { Log.Warn("Settings.Delete", ex); }
    }

    private const int CurrentVersion = 7;
    public int SettingsVersion { get; set; } = CurrentVersion;

    private static readonly object SaveGate = new();

    public void Save()
    {
        lock (SaveGate)
        try
        {
            // 1.3.0-beta.1 (review #8): atomic. Write .tmp, flush to disk, then swap it in and keep the old file as .bak.
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs)) { w.Write(json); w.Flush(); fs.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, FilePath);
        }
        catch (Exception ex) { Log.Warn("Settings.Save", ex); }
    }
}

/// <summary>ov.3: one overlay level's look. Defaults = SteamOS (top left, 100 %, 50 % background, no move).</summary>
public sealed class OverlayLook
{
    public int Position { get; set; } = 0;
    public int Size { get; set; } = 100;
    public int Bg { get; set; } = 50;
    public int X { get; set; } = 0;
    public int Y { get; set; } = 0;
}
