using System.Collections.Generic;
using AlienControl.Services;
using Xunit;

namespace AlienControl.Tests;

public class CliParsing
{
    [Fact]
    public void Sensors_fans_and_temps()
    {
        var s = AlienFan.ParseSensors("Supported hardware: 2 fans\nCPU Internal Thermistor: 62\nGPU Internal Thermistor: 55\nFan CPU 0 RPM: 3100\nFan GPU 1 RPM: 2950\nFan 2 RPM: 0\n");
        Assert.Equal(3, s.Fans.Count);
        Assert.Equal("CPU", s.Fans[0].Type);
        Assert.Equal(2950, s.Fans[1].Rpm);
        Assert.Equal(2, s.Temps.Count);
        Assert.Equal(62, s.Temps[0].Celsius);
    }

    [Theory]
    [InlineData("G-mode is On", true)]
    [InlineData("G-Mode Off", false)]
    [InlineData("garbage", null)]
    public void GMode(string output, bool? expected) => Assert.Equal(expected, AlienFan.ParseGMode(output));

    [Theory]
    [InlineData("Power mode: Level 160 (a0)", 0xA0)]
    [InlineData("Power mode: Level 165 (A5)", 0xA5)]
    [InlineData("nothing", null)]
    public void PowerRaw(string output, int? expected) => Assert.Equal(expected, AlienFan.ParsePowerRaw(output));
}

public class GpuModes
{
    static GpuMode.Adapter Intel(int err = 0) => new("Intel(R) Iris(R) Xe Graphics", "PCI\\VEN_8086", err);
    static GpuMode.Adapter Nv(int err = 0) => new("NVIDIA GeForce RTX 3070 Ti Laptop GPU", "PCI\\VEN_10DE", err);

    [Fact] public void No_nvidia_is_unknown() => Assert.Equal(GpuModeKind.Unknown, GpuMode.Current(new List<GpuMode.Adapter> { Intel() }, null, false));
    [Fact] public void No_intel_is_ultimate() => Assert.Equal(GpuModeKind.Ultimate, GpuMode.Current(new List<GpuMode.Adapter> { Nv() }, null, false));
    [Fact] public void Rtx_disabled_is_igpu_only() => Assert.Equal(GpuModeKind.IGpuOnly, GpuMode.Current(new List<GpuMode.Adapter> { Intel(), Nv(22) }, null, false));
    [Fact] public void Panel_on_rtx_is_dgpu_only() => Assert.Equal(GpuModeKind.DGpuOnly, GpuMode.Current(new List<GpuMode.Adapter> { Intel(), Nv() }, true, false));
    [Fact] public void Panel_on_intel_is_optimus() => Assert.Equal(GpuModeKind.Optimus, GpuMode.Current(new List<GpuMode.Adapter> { Intel(), Nv() }, false, false));
}

public class SettingsTests
{
    [Fact]
    public void Old_versions_are_migrated()
    {
        var s = Settings.Parse("{\"SettingsVersion\":3,\"PerformancePowerIndex\":5,\"ChassisLights\":[0,2,3],\"Theme\":\"x\"}")!;
        Assert.Equal(2, s.PerformancePowerIndex);
        Assert.Equal(7, s.SettingsVersion);
    }

    [Fact]
    public void Fluent_theme_setting_from_1_3_is_ignored()
    {
        // 2.0: Legend only. A 1.3.x file with "Theme":"Fluent" still loads, keeps its other values and is bumped to v7.
        var s = Settings.Parse("{\"SettingsVersion\":6,\"Theme\":\"Fluent\",\"OcInPerformance\":true,\"F3Opens\":false}")!;
        Assert.Equal(7, s.SettingsVersion);
        Assert.True(s.OcInPerformance);
        Assert.False(s.F3Opens);
        Assert.True(s.F4Overlay);
        Assert.DoesNotContain("Theme", System.Text.Json.JsonSerializer.Serialize(s));
    }

    [Fact]
    public void OC_GMode_scope_defaults_on_for_older_files()
    {
        // 2.0.0-beta1.2: G-MODE and + PERF are separate. A file without OcInGMode keeps the old behaviour (G-Mode on).
        var s = Settings.Parse("{\"SettingsVersion\":7,\"OcInPerformance\":true}")!;
        Assert.True(s.OcInGMode);
        Assert.True(s.OcInPerformance);
        var off = Settings.Parse("{\"SettingsVersion\":7,\"OcInGMode\":false,\"OcInPerformance\":false}")!;
        Assert.False(off.OcInGMode);
        Assert.False(off.OcInPerformance);
    }

    // 2.0.0-beta1.3 (services review #2): no readable settings = the driver state is unknown, so the next start writes +0
    [Fact]
    public void Defaults_fallback_marks_oc_pending()
    {
        var d = Settings.Defaults();
        Assert.True(d.OcPending);
        Assert.True(d.LoadedDefaults);
        Assert.False(d.OcOn);
        var json = System.Text.Json.JsonSerializer.Serialize(d);
        Assert.DoesNotContain("LoadedDefaults", json);   // never saved
        var back = Settings.Parse(json)!;
        Assert.True(back.OcPending);
        Assert.False(back.LoadedDefaults);
    }

    [Fact] public void Too_old_is_rejected() => Assert.Null(Settings.Parse("{\"SettingsVersion\":1}"));

    [Fact]
    public void Clamp_keeps_hardware_values_safe()
    {
        var s = Settings.Parse("{\"SettingsVersion\":7,\"QuietPowerIndex\":99,\"PerformancePowerIndex\":5,\"ChassisLights\":[0,2,3,40],\"Brightness\":999,\"OcCore\":5000,\"KbdTimeoutMin\":7,\"LastEffect\":5}")!;
        s.Clamp();
        Assert.Equal(4, s.QuietPowerIndex);
        Assert.Equal(2, s.PerformancePowerIndex);
        Assert.Equal(new[] { 0, 3 }, s.ChassisLights);   // never the power button (2), nothing out of range
        Assert.Equal(204, s.Brightness);
        Assert.Equal(0, s.OcCore);
        Assert.Equal(0, s.KbdTimeoutMin);
        Assert.Equal(0, s.LastEffect);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    public void Spectrum_and_rainbow_survive_a_restart(int fx)
    {
        // 1.3.1: Clamp used to reset the app's own effects (100 Spectrum, 101 Rainbow wave) to Static
        var s = Settings.Parse("{\"SettingsVersion\":6,\"LastEffect\":" + fx + "}")!;
        s.Clamp();
        Assert.Equal(fx, s.LastEffect);
    }

    [Fact]
    public void Black_from_130_off_becomes_three_off_switches()
    {
        var s = Settings.Parse("{\"SettingsVersion\":6,\"LastColor\":\"AAAA\"}")!;
        s.Clamp();
        Assert.True(s.OffKeyboard && s.OffRing && s.OffAlienhead);
        Assert.Equal(new byte[] { 0, 229, 255 }, s.LastColor);
    }

    [Fact]
    public void Lights_off_switches_default_on_and_are_kept()
    {
        var d = Settings.Parse("{\"SettingsVersion\":6,\"LastColor\":\"/01q\"}")!;
        d.Clamp();
        Assert.False(d.OffKeyboard || d.OffRing || d.OffAlienhead);
        var s = Settings.Parse("{\"SettingsVersion\":6,\"LastColor\":\"/01q\",\"OffRing\":true}")!;
        s.Clamp();
        Assert.True(s.OffRing);
        Assert.False(s.OffKeyboard);
        Assert.Equal(new byte[] { 255, 77, 106 }, s.LastColor);
    }

    [Fact]
    public void Beta2_upgrade_gets_soft_and_pill()
    {
        // a beta.1 file has neither key: Soft chimes + Pill toasts, the user's other values kept
        var s = Settings.Parse("{\"SettingsVersion\":6,\"Toasts\":false,\"KbdTimeoutMin\":5}")!;
        s.Clamp();
        Assert.Equal("Soft", s.ChimePack);
        Assert.Equal("Pill", s.ToastStyle);
        Assert.False(s.Toasts);
        Assert.Equal(5, s.KbdTimeoutMin);
    }

    [Fact]
    public void Beta2_unknown_values_fall_back()
    {
        var s = Settings.Parse("{\"SettingsVersion\":6,\"ChimePack\":\"Loud\",\"ToastStyle\":\"x\"}")!;
        s.Clamp();
        Assert.Equal("Soft", s.ChimePack);
        Assert.Equal("Pill", s.ToastStyle);
        var c = Settings.Parse("{\"SettingsVersion\":6,\"ChimePack\":\"Classic\",\"ToastStyle\":\"Classic\"}")!;
        c.Clamp();
        Assert.Equal("Classic", c.ChimePack);
        Assert.Equal("Classic", c.ToastStyle);
    }
}

public class AutoPreset
{
    [Theory]
    [InlineData(TrayProfile.Quiet, true, TrayProfile.Balanced)]
    [InlineData(TrayProfile.Balanced, true, TrayProfile.Performance)]
    [InlineData(TrayProfile.Performance, true, TrayProfile.Quiet)]
    [InlineData(TrayProfile.Performance, false, TrayProfile.Battery)]
    [InlineData(TrayProfile.Battery, false, TrayProfile.Quiet)]
    [InlineData(TrayProfile.Custom, true, TrayProfile.Quiet)]
    public void F2_cycle(TrayProfile cur, bool ac, TrayProfile next) => Assert.Equal(next, Profiles.NextPreset(cur, ac));

    [Fact] public void Unplug_remembers_ac_preset() => Assert.Equal(TrayProfile.Performance, Profiles.RememberOnUnplug(TrayProfile.Performance, false));
    [Fact] public void Unplug_from_gmode_remembers_balanced() => Assert.Equal(TrayProfile.Balanced, Profiles.RememberOnUnplug(TrayProfile.Performance, true));
    [Fact] public void Unplug_on_battery_stays() => Assert.False(Profiles.SwitchOnUnplug(TrayProfile.Battery, false));
    [Fact] public void Plug_restores_saved() => Assert.Equal(TrayProfile.Quiet, Profiles.RestoreOnPlug(TrayProfile.Battery, false, "Quiet"));
    [Fact] public void Plug_keeps_manual_pick() => Assert.Null(Profiles.RestoreOnPlug(TrayProfile.Quiet, false, "Balanced"));
    [Fact] public void Plug_bad_saved_is_balanced() => Assert.Equal(TrayProfile.Balanced, Profiles.RestoreOnPlug(TrayProfile.Battery, false, "Battery"));
}

public class StartupTask
{
    [Fact]
    public void Xml_has_no_battery_conditions()
    {
        var x = Startup.TaskXml(@"C:\Program Files\Alien Controls\AlienControl.exe", @"PC\amine");
        Assert.Contains("<DisallowStartIfOnBatteries>false", x);
        Assert.Contains("<StopIfGoingOnBatteries>false", x);
        Assert.Contains("<ExecutionTimeLimit>PT0S", x);
        Assert.False(Startup.NeedsRebuild(x));
        Assert.True(Startup.NeedsRebuild("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>"));
    }

    // 2.0.0-beta1.3 (services review #4): a task that launches another exe (the zip extracted somewhere else) is rebuilt
    [Fact]
    public void Task_for_another_exe_is_rebuilt()
    {
        const string exe = @"C:\Program Files\Alien Controls\AlienControl.exe";
        var x = Startup.TaskXml(exe, @"PC\amine");
        Assert.Equal(exe, Startup.TaskCommand(x));
        Assert.False(Startup.NeedsRebuild(x, exe));
        Assert.False(Startup.NeedsRebuild(x, exe.ToUpperInvariant()));   // Windows paths are case-insensitive
        Assert.True(Startup.NeedsRebuild(x, @"D:\Tools\Alien Controls 2\AlienControl.exe"));
        Assert.True(Startup.NeedsRebuild(x.Replace("<Command>", "<Cmd>").Replace("</Command>", "</Cmd>"), exe));   // no command at all
    }

    [Fact]
    public void Task_command_with_xml_escapes_matches()
    {
        const string exe = @"C:\Games & Tools\Alien Controls\AlienControl.exe";
        var x = Startup.TaskXml(exe, @"PC\amine");
        Assert.Contains("&amp;", x);
        Assert.False(Startup.NeedsRebuild(x, exe));
    }
}

public class SoundsWav
{
    private static byte[] Wav(ushort tag, ushort ch, uint rate, ushort bits, int dataLen)
    {
        var ms = new System.IO.MemoryStream(); var w = new System.IO.BinaryWriter(ms);
        w.Write(0x46464952u); w.Write(36 + dataLen); w.Write(0x45564157u);
        w.Write(0x20746D66u); w.Write(16); w.Write(tag); w.Write(ch); w.Write(rate); w.Write(rate * ch * bits / 8u);
        w.Write((ushort)(ch * bits / 8)); w.Write(bits);
        w.Write(0x61746164u); w.Write(dataLen); w.Write(new byte[dataLen]);
        return ms.ToArray();
    }

    [Fact] public void Reads_16bit_pcm() => Assert.Equal(((ushort)1, 48000u, 44, 960), Sounds.ReadWav(Wav(1, 1, 48000, 16, 960)));
    [Fact] public void Rejects_float_and_8bit() { Assert.Null(Sounds.ReadWav(Wav(3, 1, 48000, 32, 960))); Assert.Null(Sounds.ReadWav(Wav(1, 1, 48000, 8, 960))); }
    [Fact] public void Rejects_truncated() { var b = Wav(1, 1, 48000, 16, 960); Assert.Null(Sounds.ReadWav(b[..100])); }
}
