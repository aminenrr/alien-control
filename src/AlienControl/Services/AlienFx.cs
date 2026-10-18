using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AlienControl.Services;

/// <summary>
/// Thin wrapper over alienfx-cli (lighting).
/// The m15 R7 keyboard is per-key RGB and its light IDs don't follow key order, so colors are
/// sent to every ID with chained setone commands instead of setall (which needs probe names).
/// Command reference: https://github.com/T-Troll/alienfx-tools/blob/master/Doc/alienfx-cli.md
/// </summary>
public sealed class AlienFx
{
    private readonly CliRunner _cli = new("alienfx-cli.exe");
    private readonly Settings _s;

    public AlienFx(Settings settings) => _s = settings;

    public bool IsAvailable => _cli.IsAvailable;

    private readonly KeyboardHid _hid = new();
    public KeyboardHid Hid => _hid;

    // ---------- 1.3.1: lights the user turned off (Lighting > Off menu) ----------

    /// <summary>True when this chassis light is turned off: the rear ring (IDs 0 + 1) or the lid Alienhead (ID 3).</summary>
    private bool ChassisOff(int id) => Settings.RingLights.Contains(id) ? _s.OffRing : id == Settings.AlienheadLight && _s.OffAlienhead;

    /// <summary>Chassis commands for every light except the power button: act(id) for lit ones, black for turned-off ones.</summary>
    private string Chassis(System.Func<int, string> act) => string.Join(" ", _s.ChassisLights.Where(i => i != Settings.PowerButtonLight)
        .Select(i => ChassisOff(i) ? $"setone={_s.ChassisDevice},{i},0,0,0" : act(i)));

    /// <summary>Keyboard off: brightness 0 and frames held (KeyboardHid). Re-sent each time, since resume can reset it.</summary>
    private Task KeyboardOffAsync() => _s.OffKeyboard
        ? Task.Run(() => { try { _hid.SetOff(true); } catch (System.Exception e) { Log.Warn("AlienFx.KeyboardOff", e); } })
        : Task.CompletedTask;

    /// <summary>Scales a color by brightness 0..255 (software dimming).</summary>
    public static byte[] Dim(byte[] c, int brightness) =>
        c.Select(v => (byte)(v * System.Math.Clamp(brightness, 0, 255) / 255)).ToArray();

    /// <summary>
    /// Static color on every keyboard key and the chassis lights (status keys that are on get the
    /// indicator color). Brightness is applied in SOFTWARE by scaling the colors: hardware setdim
    /// on the chassis also dims the power button (which must stay firmware-controlled) and can't
    /// reach 0, and on the keyboard every color frame resets it (tested 2026-10-15).
    /// The keyboard goes out as ONE HID frame (see KeyboardHid); alienfx-cli is only the fallback.
    /// </summary>
    public async Task<CliResult> SetAllAsync(byte r, byte g, byte b, IReadOnlyDictionary<int, bool>? indicatorsOn, int brightness)
    {
        var c = Dim(new[] { r, g, b }, brightness);
        var w = Dim(_s.IndicatorOnColor, brightness);
        await KeyboardOffAsync();
        var chassis = Chassis(i => $"setone={_s.ChassisDevice},{i},{c[0]},{c[1]},{c[2]}");
        // 1.3.1: a turned-off keyboard gets no setglobal (it could light it back up); its frame is held by KeyboardHid
        var res = await _cli.RunAsync(_s.OffKeyboard ? chassis : $"setglobal={_s.KeyboardDevice},0,1 {chassis}");
        if (await SetKeyboardAsync(new[] { r, g, b }, indicatorsOn, brightness)) return res;
        if (_s.OffKeyboard) return res;

        // Fallback: per-key CLI (only the last status key will stay lit).
        var cmds = Enumerable.Range(0, _s.KeyboardLightCount).Select(i =>
            indicatorsOn is not null && indicatorsOn.TryGetValue(i, out var on) && on
                ? $"setone={_s.KeyboardDevice},{i},{w[0]},{w[1]},{w[2]}"
                : $"setone={_s.KeyboardDevice},{i},{c[0]},{c[1]},{c[2]}");
        return await _cli.RunAsync(string.Join(" ", cmds));
    }

    /// <summary>v0.9.10: per-key colors on top of the base color (F2 preset tint). Status keys still win.</summary>
    public System.Collections.Generic.Dictionary<int, byte[]> KeyOverrides { get; } = new();

    private byte[] Base(int i, byte[] fallback) => KeyOverrides.TryGetValue(i, out var o) ? o : fallback;

    /// <summary>All keyboard keys in one frame, colors scaled by brightness. False if the HID path failed.</summary>
    public Task<bool> SetKeyboardAsync(byte[] baseColor, IReadOnlyDictionary<int, bool>? indicatorsOn, int brightness)
    {
        var w = Dim(_s.IndicatorOnColor, brightness);
        var bc = Dim(baseColor, brightness);
        var keys = Enumerable.Range(0, _s.KeyboardLightCount).Select(i =>
        {
            var c = indicatorsOn is not null && indicatorsOn.TryGetValue(i, out var on) && on ? w
                  : KeyOverrides.TryGetValue(i, out var o) ? Dim(o, brightness) : bc;
            return ((byte)i, c[0], c[1], c[2]);
        }).ToList();
        return Task.Run(() => { try { return _hid.SetKeys(keys); } catch { return false; } });
    }

    /// <summary>
    /// Undoes old hardware dimming from v0.5.x: full hardware brightness on both devices, so the
    /// power button is back at its firmware level. Software dimming takes over from here.
    /// </summary>
    public async Task ResetHardwareDimAsync()
    {
        await Task.Run(() => { try { _hid.SetBrightness(_s.OffKeyboard ? 0 : 255); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("AlienFx.cs:77", __e); } });
        await _cli.RunAsync($"setdim={_s.ChassisDevice},255");
    }

    /// <summary>One keyboard key.</summary>
    public Task<CliResult> SetKeyAsync(int id, byte r, byte g, byte b) =>
        _cli.RunAsync($"setone={_s.KeyboardDevice},{id},{r},{g},{b}");

    /// <summary>
    /// APIv5 keyboard effect. Types: 0 Color, 2 Breathing, 3 Single-color wave, 4 Dual-color wave,
    /// 8 Pulse, 9 Mixed pulse, 10 Night Rider, 11 Laser. (No spectrum on APIv5.)
    /// </summary>
    public Task<CliResult> SetKeyboardEffectAsync(int type, byte r, byte g, byte b) =>
        _cli.RunAsync($"setglobal={_s.KeyboardDevice},{type},1,{r},{g},{b}");

    /// <summary>Second color for the two-color effects: purple, or cyan when the main color is purple.</summary>
    public static byte[] SecondColor(byte[] c) =>
        c.SequenceEqual(new byte[] { 124, 77, 255 }) ? new byte[] { 0, 229, 255 } : new byte[] { 124, 77, 255 };

    /// <summary>
    /// A keyboard effect plus the closest APIv4 action on the chassis lights (power button skipped).
    /// Colors are scaled by brightness.
    /// </summary>
    public async Task<CliResult> SetEffectAsync(int type, byte[] color, int brightness)
    {
        await KeyboardOffAsync();
        var a = Dim(color, brightness);
        var b = Dim(SecondColor(color), brightness);
        bool dual = type is 4 or 9;
        var kb = dual
            ? $"setglobal={_s.KeyboardDevice},{type},1,{a[0]},{a[1]},{a[2]},{b[0]},{b[1]},{b[2]}"
            : $"setglobal={_s.KeyboardDevice},{type},1,{a[0]},{a[1]},{a[2]}";
        string Act(int light) => type switch
        {
            2 => $"setaction={_s.ChassisDevice},{light},breath,{a[0]},{a[1]},{a[2]}",
            8 => $"setaction={_s.ChassisDevice},{light},pulse,{a[0]},{a[1]},{a[2]}",
            4 or 9 => $"setaction={_s.ChassisDevice},{light},morph,{a[0]},{a[1]},{a[2]},morph,{b[0]},{b[1]},{b[2]}",
            _ => $"setone={_s.ChassisDevice},{light},{a[0]},{a[1]},{a[2]}",
        };
        var chassis = Chassis(Act);
        return await _cli.RunAsync(_s.OffKeyboard ? chassis : $"{chassis} {kb}");
    }

    /// <summary>Firmware color-cycle on the chassis lights (APIv4 spectrum/rainbow action), power button skipped.</summary>
    public async Task<CliResult> SetChassisCycleAsync(bool rainbow)
    {
        await KeyboardOffAsync();
        var act = rainbow ? "rainbow" : "spectrum";
        var chassis = Chassis(i => $"setaction={_s.ChassisDevice},{i},{act},255,0,0");
        return await _cli.RunAsync(_s.OffKeyboard ? chassis : $"setglobal={_s.KeyboardDevice},0,1 " + chassis);
    }

    /// <summary>One software-animation frame: every key its own color (status keys white if on).</summary>
    public bool SendFrame(System.Func<int, byte[]> colorOf, IReadOnlyDictionary<int, bool> indicatorsOn, int brightness)
    {
        var w = Dim(_s.IndicatorOnColor, brightness);
        var keys = new List<(byte, byte, byte, byte)>(_s.KeyboardLightCount);
        for (int i = 0; i < _s.KeyboardLightCount; i++)
        {
            var c = indicatorsOn.TryGetValue(i, out var on) && on ? w : Dim(Base(i, colorOf(i)), brightness);
            keys.Add(((byte)i, c[0], c[1], c[2]));
        }
        try { return _hid.SetKeys(keys); } catch { return false; }
    }

    public Task<CliResult> StatusAsync() => _cli.RunAsync("status");
}
