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

    /// <summary>
    /// Static color on every keyboard key and the chassis lights. Status keys that are on get the
    /// indicator color. The keyboard goes out as ONE HID frame (see KeyboardHid for why);
    /// alienfx-cli is only the fallback.
    /// </summary>
    public async Task<CliResult> SetAllAsync(byte r, byte g, byte b, IReadOnlyDictionary<int, bool>? indicatorsOn, int brightness)
    {
        var chassis = string.Join(" ", _s.ChassisLights.Where(i => i != Settings.PowerButtonLight)
            .Select(i => $"setone={_s.ChassisDevice},{i},{r},{g},{b}"));
        var res = await _cli.RunAsync($"setglobal={_s.KeyboardDevice},0,1 {chassis} setdim={_s.ChassisDevice},{brightness}");
        if (await SetKeyboardAsync(new[] { r, g, b }, indicatorsOn, brightness)) return res;

        // Fallback: per-key CLI (only the last status key will stay lit).
        var w = _s.IndicatorOnColor;
        var cmds = Enumerable.Range(0, _s.KeyboardLightCount).Select(i =>
            indicatorsOn is not null && indicatorsOn.TryGetValue(i, out var on) && on
                ? $"setone={_s.KeyboardDevice},{i},{w[0]},{w[1]},{w[2]}"
                : $"setone={_s.KeyboardDevice},{i},{r},{g},{b}");
        return await _cli.RunAsync(string.Join(" ", cmds) + $" setdim={_s.KeyboardDevice},{brightness}");
    }

    /// <summary>All keyboard keys in one frame: base color, white for active status keys. False if the HID path failed.</summary>
    public Task<bool> SetKeyboardAsync(byte[] baseColor, IReadOnlyDictionary<int, bool>? indicatorsOn, int brightness)
    {
        var w = _s.IndicatorOnColor;
        var keys = Enumerable.Range(0, _s.KeyboardLightCount).Select(i =>
        {
            var c = indicatorsOn is not null && indicatorsOn.TryGetValue(i, out var on) && on ? w : baseColor;
            return ((byte)i, c[0], c[1], c[2]);
        }).ToList();
        return Task.Run(() => { try { return _hid.SetKeys(keys, brightness); } catch { return false; } });
    }

    /// <summary>One keyboard key.</summary>
    public Task<CliResult> SetKeyAsync(int id, byte r, byte g, byte b) =>
        _cli.RunAsync($"setone={_s.KeyboardDevice},{id},{r},{g},{b}");

    /// <summary>Hardware brightness 0..255 on keyboard and chassis.</summary>
    public async Task<CliResult> SetBrightnessAsync(int level)
    {
        bool hid = await Task.Run(() => { try { return _hid.SetBrightness(level); } catch { return false; } });
        return await _cli.RunAsync(hid ? $"setdim={_s.ChassisDevice},{level}" : $"setdim={_s.KeyboardDevice},{level} setdim={_s.ChassisDevice},{level}");
    }

    /// <summary>
    /// APIv5 keyboard effect. Types: 0 Color, 2 Breathing, 3 Single-color wave, 4 Dual-color wave,
    /// 8 Pulse, 9 Mixed pulse, 10 Night Rider, 11 Laser. (No spectrum on APIv5.)
    /// </summary>
    public Task<CliResult> SetKeyboardEffectAsync(int type, byte r, byte g, byte b) =>
        _cli.RunAsync($"setglobal={_s.KeyboardDevice},{type},1,{r},{g},{b}");

    public Task<CliResult> StatusAsync() => _cli.RunAsync("status");
}
