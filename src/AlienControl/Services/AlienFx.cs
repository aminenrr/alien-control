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

    /// <summary>
    /// Static color on every keyboard key and the chassis lights. Status keys listed in
    /// <paramref name="indicatorsOn"/> that are on get the indicator color instead.
    /// </summary>
    public Task<CliResult> SetAllAsync(byte r, byte g, byte b, IReadOnlyDictionary<int, bool>? indicatorsOn = null)
    {
        var w = _s.IndicatorOnColor;
        var cmds = new List<string> { $"setglobal={_s.KeyboardDevice},0,1" }; // stop any keyboard effect
        cmds.AddRange(Enumerable.Range(0, _s.KeyboardLightCount).Select(i =>
            indicatorsOn is not null && indicatorsOn.TryGetValue(i, out var on) && on
                ? $"setone={_s.KeyboardDevice},{i},{w[0]},{w[1]},{w[2]}"
                : $"setone={_s.KeyboardDevice},{i},{r},{g},{b}"));
        cmds.AddRange(_s.ChassisLights.Where(i => i != Settings.PowerButtonLight).Select(i => $"setone={_s.ChassisDevice},{i},{r},{g},{b}"));
        return _cli.RunAsync(string.Join(" ", cmds));
    }

    /// <summary>
    /// The five firmware status keys as their own command: white when on, the base color when off.
    /// Must run after SetAllAsync, whose 136-key batch leaves these keys dark.
    /// </summary>
    public async Task<CliResult> SetIndicatorsAsync(IReadOnlyDictionary<int, bool> states, byte[] baseColor)
    {
        // One CLI call per key: in a chained command only the last status key takes effect
        // (v0.5 test: right Win lit, F1/F12/mutes stayed dark), while single calls always work.
        var w = _s.IndicatorOnColor;
        var last = new CliResult(true, "", "");
        foreach (var kv in states)
        {
            var c = kv.Value ? w : baseColor;
            last = await _cli.RunAsync($"setone={_s.KeyboardDevice},{kv.Key},{c[0]},{c[1]},{c[2]}");
        }
        return last;
    }

    /// <summary>One keyboard key.</summary>
    public Task<CliResult> SetKeyAsync(int id, byte r, byte g, byte b) =>
        _cli.RunAsync($"setone={_s.KeyboardDevice},{id},{r},{g},{b}");

    /// <summary>Hardware brightness 0..255 on keyboard and chassis.</summary>
    public Task<CliResult> SetBrightnessAsync(int level) =>
        _cli.RunAsync($"setdim={_s.KeyboardDevice},{level} setdim={_s.ChassisDevice},{level}");

    /// <summary>
    /// APIv5 keyboard effect. Types: 0 Color, 2 Breathing, 3 Single-color wave, 4 Dual-color wave,
    /// 8 Pulse, 9 Mixed pulse, 10 Night Rider, 11 Laser. (No spectrum on APIv5.)
    /// </summary>
    public Task<CliResult> SetKeyboardEffectAsync(int type, byte r, byte g, byte b) =>
        _cli.RunAsync($"setglobal={_s.KeyboardDevice},{type},1,{r},{g},{b}");

    public Task<CliResult> StatusAsync() => _cli.RunAsync("status");
}
