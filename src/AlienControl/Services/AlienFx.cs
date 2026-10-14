using System.Threading.Tasks;

namespace AlienControl.Services;

/// <summary>
/// Thin wrapper over alienfx-cli (lighting). No admin needed.
/// Run "alienfx-cli probe" once so lights are named, or setall does nothing on low-level API.
/// Command reference: https://github.com/T-Troll/alienfx-tools/blob/master/Doc/alienfx-cli.md
/// </summary>
public sealed class AlienFx
{
    private readonly CliRunner _cli = new("alienfx-cli.exe");

    public bool IsAvailable => _cli.IsAvailable;

    public Task<CliResult> SetAllAsync(byte r, byte g, byte b) => _cli.RunAsync($"setall={r},{g},{b}");

    public Task<CliResult> OffAsync() => SetAllAsync(0, 0, 0);

    /// <summary>Hardware brightness 0..255 for one device.</summary>
    public Task<CliResult> SetBrightnessAsync(int device, int level) => _cli.RunAsync($"setdim={device},{level}");

    /// <summary>Global hardware effect (APIv5/APIv8 keyboards). Type ids differ per API, see docs.</summary>
    public Task<CliResult> SetGlobalEffectAsync(int device, int type, int mode = 1) =>
        _cli.RunAsync($"setglobal={device},{type},{mode}");

    public Task<CliResult> StatusAsync() => _cli.RunAsync("status");
}
