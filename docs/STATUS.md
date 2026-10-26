# Project status (for anyone picking this up)

Last updated: 2026-10-26, **v2.0.2** (stable) on `main` and `v2.0`. The previous stable was v1.3.5 (branch `v1.3`).
Read this first, then [DEVELOPMENT.md](DEVELOPMENT.md) (code layout, build), [DECISIONS.md](DECISIONS.md) (why),
[HARDWARE.md](HARDWARE.md) (what was measured on the m15 R7) and the 2.0 spec in
[mockups/2.0/HANDOFF.md](mockups/2.0/HANDOFF.md).

## What the app is
A C# / .NET 8 WPF tray app that replaces Alienware Command Center on one Alienware m15 R7: power presets, G-Mode,
fans, GPU mode (Optimus / iGPU only), NVIDIA overclock, lighting (keyboard HID frames, rear ring, Alienhead),
status keys, toasts and chimes. One 440 x 820 window (Legend theme only), tabs Home / Tuning / Lights / Overlay,
a two-tab Settings page. Runs elevated, starts at logon through a scheduled task.

## Branches
See [BRANCHES.md](BRANCHES.md). `main` = stable 2.0, `v2.0` = work branch, `v1.3` = the old 1.x line. The two agent
setup of the 2.0 betas (`beta1.3-ov`, `merge/beta2`) is over and those branches are deleted; the old 1.x beta checklists
are in `docs/archive/`.

## Architecture
**App** (`App.xaml.cs`): single instance, tray icon and menu, theme loading (`ApplyTheme`: Legend.xaml then
Legend2.xaml), F3 open / hide, crash log (`%AppData%\AlienControl\crash.log`), crash and session-end cleanup
(`CrashCleanup`: OC back to +0, CPU cap cleared, pending settings flushed), non-fatal UI exceptions shown in the status
line (a burst of more than 5 in 10 s, or a fatal type, still runs the cleanup).

**MainWindow** partials (one class, split in beta1.4 without behaviour change):
| File | Holds |
|---|---|
| `MainWindow.xaml.cs` | fields, constructor, startup, OnClosing / Quit, accent (`ApplyAccent`, `AccentTints`), hints, swatches, status line, RAM clean, Reset settings |
| `MainWindow.Window.cs` | placement, short-screen scaling (`FitToWorkArea`, under 848 DIP), slide, memory trim, `AllowClose` (also flushes settings) |
| `MainWindow.Sensors.cs` | sensor and state reads (off the UI thread), Home numbers, power line |
| `MainWindow.Modes.cs` | the command gate (`_cmdGate`), power modes, G-Mode, CPU and fan boost, refresh rate |
| `MainWindow.Gpu.cs` | GPU MODE row, RTX off / on, GPU dialog |
| `MainWindow.Lights.cs` | colours, effects, animation, brightness, zones, status keys |
| `MainWindow.Presets.cs` | presets, tray profiles, F2, chip, header preset button |
| `MainWindow.Oc.cs` | GPU overclock (G-MODE / + PERF scope, keep-or-revert, toast tag) |
| `MainWindow.Tabs.cs` | tab switching, polling only for the tab on screen, Home tiles and G-Mode switch |
| `MainWindow.Settings.cs` | Settings page (two tabs) |
| `MainWindow.Kbd.cs`, `MainWindow.Auto.cs` | keyboard backlight timeout, auto preset on plug / unplug |

**Services** (`src/AlienControl/Services`):
- `AlienFan` power modes, G-Mode, boost, fans through alienfan-cli; `AwccSensors` direct WMI temps and fan RPM.
- `AlienFx` chassis lighting through the CLI; `KeyboardHid` whole-keyboard HID frames, brightness, fade and wake.
- `NvmlSession` the one NVML owner (lock, handle, `BeginDeviceRemoval` / `EndDeviceRemoval` around pnputil);
  `Nvml`, `GpuOc` (offsets, `Read` at startup), `PowerMeter` (RAPL, NVML power, battery; closes NVML on battery and
  when polling stops).
- `GpuMode` mode detection, RTX disable / enable (pnputil, 60 s timeout, never killed), NVIDIA Display Container
  restart after the RTX comes back (`EnsureNvidiaTrayAsync`, service manager, waits for each state); `DellBios` MUX lookup.
- `CpuFreq` CPU cap and boost through powrprof in-process (powercfg fallback); `DisplayRefresh` 60 / 240 Hz on the
  internal panel.
- `SystemStats` RAM, VRAM and GPU % through one PDH query (beta1.4: `GpuUsage` = RTX LUID, `IntelGpuUsage` = Intel
  LUID from DXGI, same query, called only in iGPU only); `SystemState` / `StateWatch` mute, mic, touchpad.
- `IntelGpuTemp` (beta1.4) Intel GPU temperature through Intel IGCL (ControlLib.dll, System32, read-only), used for the
  GPU temp tile in iGPU only. Disabled for the session after the first failure; the tile then shows the CPU package.
- `MacroKeys` F2 / F3 / F4 macro keys (raw HID), `GKeyHook` F1, `InputThread` low-level input thread.
- `Settings` `settings.json` (versioned migrations, v7; atomic save with retry, copy fallback and a pending flush;
  `.corrupt` rename; unknown keys kept by `[JsonExtensionData]`).
- `Startup` logon task (beta1.4: admin-only copy in `%ProgramFiles%\AlienControl`, `IsAdminOnly` ACL check, Task
  Scheduler COM registration, off removes the copy), `FolderSafety`, `SysExe`, `ProcessRunner` / `CliRunner`
  (timeouts), `Toast`, `TrayMenu`, `Sounds`, `WindowSlide`, `MemoryCleaner`, `Profiles`, `Log`, `HardwareController`.

**Themes**: `Themes/Legend.xaml` (base palette, icons) and `Themes/Legend2.xaml` (2.0 styles: tabs, tiles, pills,
toggles, cards, FocusRing2, ToolTip, TextBox, ScrollBar). Both are hand-edited; the old generator is retired in
`dev/retired/gen_themes.py`. `ApplyAccent` publishes `AccentSelBg` (16 %) and `AccentPillBg` (22 %) as tints of the
lighting colour. `python dev/check_xaml_keys.py` checks for unreachable, missing and duplicate keys (CI runs it).
No third-party UI library since beta1.4 (WPF-UI removed).

## Shipped in 2.0
- **beta1**: the new look (tabs, Legend only, no compact view, two-tab Settings, Overlay tab as "coming in beta 2").
- **beta1.1**: startup crash fix (duplicate `Card2` key), CI XAML duplicate-key check.
- **beta1.2**: strip order CPU | GPU | PWR | FANS with dividers (Home PWR strip too), independent G-MODE and + PERF
  OC switches (both off = no OC), opening always goes to Home and closes Settings.
- **beta1.3**: Tier 1 review fixes: shared NvmlSession, OC and CPU cap cleanup on crash / logoff / shutdown, startup OC
  read with safe defaults, Reset settings cleanup, one light animation at a time, serialized commands (F1, presets,
  power modes, tray, auto preset, resume), dirty / retry follow-ups, no polling while minimized, keyboard fade / wake
  race, startup task XML lock and exe path, refresh rate on the internal panel, process timeouts. Lights hint reads
  CTRL + ALT + F · LIGHT LEVEL. Tested on the laptop: all good.
- **beta1.4**: Tier 2 and Tier 3, settings save fix, six visual changes (the sixth: INTEL / NVIDIA tags on the GPU
  tiles, `mockups/2.0-beta1.4/6-gpu-source.png`), startup task security fix (Program Files
  copy), Log frame gaps removed, NVIDIA tray icon after iGPU only, OC card greyed while the RTX is off. See
  [releases/v2.0.0-beta1.4.md](releases/v2.0.0-beta1.4.md). Tested on the laptop 2026-10-24: all good. The NVIDIA tray icon missed once right after the update, then came back 3 out of 3 after a reboot. Its startup copy skipped alienfan-cli.exe / alienfx-cli.exe kept next to the exe (sensors and shortcuts dead after a reboot): fixed in **v2.0.0-beta1.5** ([releases/v2.0.0-beta1.5.md](releases/v2.0.0-beta1.5.md)), which also makes the NVIDIA tray check wait for NVDisplay.Container. Tested OK on the laptop 2026-10-24 (after quitting beta1.4 first). **v2.0.0-beta1.6** ([releases/v2.0.0-beta1.6.md](releases/v2.0.0-beta1.6.md)): a newer copy takes over from the running one (Local\AlienControls.Quit event + mutex hand over), and the boot-time Startup.GetTask warning is gone. Tested OK on the laptop 2026-10-24; the hand over itself is first exercised by the next version (beta2). IGCL has no GPU temperature sensor on the m15 R7, so iGPU temp is the CPU package.

- **beta2 to beta3.6 (the in-game overlay)**: PresentMon frametimes (no injection, click through window, Direct2D
  with a WPF fallback), 4 levels plus Advanced, F4 / Shift + F4, latency toggle, per game FPS cap and V-Sync through
  the NVIDIA profile (`Services/NvDrs.cs`), level 2 in the Apple Metal HUD style (JetBrains Mono, bundled). See the
  CHANGELOG for each step.
- **2.0.0** (2026-10-25): stable, tested on the laptop.
- **2.0.1** (2026-10-26): overlay Move counted from the corner (NVIDIA style, `OverlayLook.Place`, old values migrated
  once through `Settings.OverlayMoveV2`), PERF / G-MODE pill in each mode's own colour, Metal level `--` when idle and
  G-SYNC from `NvAPI_Disp_GetVRRInfo` (refresh guess as fallback). Tested on the laptop.
- **2.0.2** (2026-10-26): stability fixes from the 2026-10-26 audit (PR #24). Resume in G-Mode keeps an unknown (null)
  G-Mode read unknown and retries once; a timed out alienfan-cli is killed and its exit confirmed before the hardware
  queue moves on; quitting awaits an in-flight OC write before the +0 reset; a second `pnputil` GPU switch is refused
  while one is pending; the `OcPending` recovery marker is only cleared by a confirmed +0 (or a committed Keep), and
  startup recovery waits until the RTX is back. Tests in `tests/AlienControl.Tests/StabilityTests.cs`. Tested on the
  laptop.

## Still to do
- Real 2.0 screenshots in the README (the mockups stand in for now).
- Overlay redraws can trail the game under G-SYNC in heavy titles (DWM bound); the perf log tracks it.
- Slide `GC.TryStartNoGCRegion` change from the review: needs an A/B on the laptop.
- With Rainbow / Spectrum, fills use cyan but text keeps the last picked colour (decide if that should change).
- Remaining review notes not acted on: build.md #11 (DynamicResource for keys that never change) and #18 (layered
  window cost, not measured).

## Known risks / not verified on hardware
- **Fixed in beta1.4**: the startup task ran the exe as admin from a user-writable folder (Downloads / Desktop), so any
  unelevated program could swap the exe or side-load a DLL next to it. It now only runs the admin-only copy in
  `%ProgramFiles%\AlienControl` (see `Services/Startup.cs`). Verified on the laptop 2026-10-24 (with the beta1.5 CLI copy fix).
- beta1.4 to beta1.6 passed Amine's What to test lists on 2026-10-24 (powrprof, NVML close / reopen, PDH GPU % and
  VRAM, WPF-UI removal, focus ring, short-screen scaling, NVIDIA tray restart, OC lock, GPU source chips).
- **GPU sensors by mode (beta1.4)**: in Advanced Optimus / Ultimate the GPU temp is the AWCC WMI sensor 0x06 (board
  sensor by the RTX) and GPU USE the RTX's PDH 3D engines (tag NVIDIA). In iGPU only the AWCC 0x06 sensor is not the
  Intel GPU, so the temp comes from IGCL and GPU USE from the Intel LUID's 3D engines (tag INTEL). IGCL status: interop
  written from Intel's public `igcl_api.h` (API 1.1, struct sizes covered by tests) but run on the laptop 2026-10-24: the m15 R7's
  Intel driver has no GPU temperature sensor through IGCL, so the CPU package fallback is what ships. crash.log says which source was
  used (`iGPU temp: IGCL` or `iGPU temp: CPU package (IGCL: reason)`); if it's always the CPU package, IGCL has no
  sensor there and the fallback is what ships. The RTX LUID is still the "most dedicated memory in use" adapter (it
  just never takes the Intel LUID once that is known).
- CI is the only place the tests run (they need Windows). The NuGet cache uses setup-dotnet `cache: true`; if it
  fails, switch to `actions/cache` on `~/.nuget/packages`.

## Releasing
1. Bump `InformationalVersion` in `src/AlienControl/AlienControl.csproj`.
2. Add a `docs/CHANGELOG.md` entry and `docs/releases/v<version>.md`. The notes always end with a **What to test**
   checklist of concrete laptop steps (Amine always wants one).
3. Push the tag `v<version>`. `release.yml` checks that the tag matches the version and that the notes file exists,
   checks XAML keys, runs the tests, publishes win-x64 and attaches the zip (with README and LICENSE) and
   `SHA256SUMS` to a GitHub release whose body is the notes file. A tag with a `-` (betas) is published as a pre-release automatically. For a stable, also fast-forward `main` to `v2.0`.

## Working from the Hark sandbox
No git push credentials there: commits go through the GitHub git data API onto a `fix/...` branch, a PR into `v2.0`
runs CI, then merge and create the tag ref (see Releasing). Local compile check: .NET 8 SDK and
`dotnet build -p:EnableWindowsTargeting=true`; the tests only run in CI (Windows).

## Conventions
- Comments tag each change with its version, plus "(Amine)" when it was his call.
- No em or en dashes anywhere in the UI. Legend only, 440 x 820, every tab the same height.
- Ask Amine before anything unclear.
