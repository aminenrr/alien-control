# Project status (for anyone picking this up)

Last updated: 2026-10-24, **v2.0.0-beta1.4** (pre-release) on branch `v2.0`. The latest stable is v1.3.5 on `main`.
Read this first, then [DEVELOPMENT.md](DEVELOPMENT.md) (code layout, build), [DECISIONS.md](DECISIONS.md) (why),
[HARDWARE.md](HARDWARE.md) (what was measured on the m15 R7) and the 2.0 spec in
[mockups/2.0/HANDOFF.md](mockups/2.0/HANDOFF.md).

## What the app is
A C# / .NET 8 WPF tray app that replaces Alienware Command Center on one Alienware m15 R7: power presets, G-Mode,
fans, GPU mode (Optimus / iGPU only), NVIDIA overclock, lighting (keyboard HID frames, rear ring, Alienhead),
status keys, toasts and chimes. One 440 x 820 window (Legend theme only), tabs Home / Tuning / Lights / Overlay,
a two-tab Settings page. Runs elevated, starts at logon through a scheduled task.

## Two branches (until 2.0.0-beta2)
Two lines of work run in parallel. The full rules are in `docs/BRANCHES-2.0.md`, which lives on branch `beta1.3-ov`
(not on `v2.0` yet): copy it into `v2.0` at the beta2 merge.
- **Agent A owns `v2.0`** (based on beta1.3, 1c26220): beta1.4 and later fixes (Tier 1 / 2 / 3 review work, WPF-UI
  removal, MainWindow partials, the startup task security fix, the Log frame gaps removal). Tags `v2.0.0-beta1.4`, ...
- **Agent B owns `beta1.3-ov`** (also based on 1c26220): the in-game overlay only (PresentMon frametimes, 4 SteamOS
  levels, F4, Overlay tab). Pre-release tags `v2.0.0-beta1.3-ov.N` (latest v2.0.0-beta1.3-ov.7, 66cf56d).
- **`main`** is 1.3.x: nobody touches it until 2.0 is stable.

Rules
- Agent A never pushes to `beta1.3-ov` or `main`; agent B never pushes to `v2.0` or `main`.
- No overlay code in `v2.0` before the beta2 merge. Never use the `v2.0.0-beta2` or `v2.0.0-beta1.3-ov.*` tags on `v2.0`.
- Overlay code lives in its own files (`src/AlienControl/Overlay/`, `Services/Frames*.cs`, `Services/CpuCores.cs`,
  `Services/VblankCounter.cs`, `MainWindow.Overlay.cs`). Its hooks in shared files are marked `// beta1.3-ov`.
- WPF-UI removal is fine for the overlay (it doesn't use WPF-UI).

Where the overlay hooks land on `v2.0` after the MainWindow split (keep these easy to find):
| Hook target | File |
|---|---|
| `RefreshSensorsAsync`, `RefreshPowerAsync` | `MainWindow.Sensors.cs` |
| `UpdatePolling` | `MainWindow.Tabs.cs` |
| `AllowClose` | `MainWindow.Window.cs` |
| `EmergencyExitCleanup` | `MainWindow.Oc.cs` |
| settings keys | `Services/Settings.cs` (the class; retired keys in `RetiredKeys`) |

Merging into 2.0.0-beta2 (either agent)
1. Wait until beta1.4 is in `v2.0` and Amine signs off the overlay on `beta1.3-ov`.
2. Branch `merge/beta2` off `v2.0`, then `git merge beta1.3-ov`.
3. Expected conflicts: `MainWindow.xaml.cs` (move each `// beta1.3-ov` hook into its partial, table above),
   `MainWindow.xaml`, `Settings.cs`, `AlienControl.csproj`, `CHANGELOG.md`, `build.yml` (drop `beta1.3-ov` from the
   branch list). Copy `docs/BRANCHES-2.0.md` in.
4. Grep `beta1.3-ov` after the merge: every hook must still be wired.
5. Bump to 2.0.0-beta2, add `docs/releases/v2.0.0-beta2.md`, PR into `v2.0`, Windows CI green, tag `v2.0.0-beta2`,
   delete `beta1.3-ov`.

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
  [releases/v2.0.0-beta1.4.md](releases/v2.0.0-beta1.4.md). Tested on the laptop 2026-10-24: all good. The NVIDIA tray icon missed once right after the update, then came back 3 out of 3 after a reboot. Open bug for the next build: the startup copy in Program Files skips alienfan-cli.exe / alienfx-cli.exe kept next to the exe (sensors and shortcuts dead after a reboot). IGCL has no GPU temperature sensor on the m15 R7, so iGPU temp is the CPU package.

## Still to do
**Beta 2 (the in-game overlay)**, in this order:
1. **SensorHub owned by App**: every read in one place on one worker thread; Home, Tuning and each overlay level
   subscribe to what they show; no subscribers means nothing polls. Required: CPU % and GPU % are "since last call"
   readers, so two readers corrupt each other.
2. **One state store and command gate** for profile, G-Mode, OC and lights, shared by the window, tray, toasts and
   overlay (today the gate lives in MainWindow).
3. **Rate limits per source**: ACPI / WMI reads at most every 1 s (hold the last value); FPS, NVML and power can run
   at overlay speed.
4. **PresentMon as a Frames source**, preferably in-process ETW (TraceEvent, MIT): fixed session name, kill a stale
   session at start, stop it on every exit path and when the last overlay subscriber goes.
5. **Overlay window** on its own UI thread, click-through, frozen brushes, one drawing surface for the graph, levels
   1 to 4 as in `mockups/2.0/07-overlay-level-*.png`; F4 toggles it.
6. **Settings**: an overlay section (unknown keys already survive a downgrade).

**Smaller open items**
- Slide `GC.TryStartNoGCRegion` change from the review: needs an A/B on the laptop.
- With Rainbow / Spectrum, fills use cyan but text keeps the last picked colour (decide if that should change).
- Tooltips use the UI font in normal case (the mockup showed mono caps); revisit if Amine wants caps.
- Remaining review notes not acted on: build.md #11 (DynamicResource for keys that never change; kept dynamic where
  the accent tint needs it) and #18 (layered window cost, not measured).

## Known risks / not verified on hardware
- **Fixed in beta1.4**: the startup task ran the exe as admin from a user-writable folder (Downloads / Desktop), so any
  unelevated program could swap the exe or side-load a DLL next to it. It now only runs the admin-only copy in
  `%ProgramFiles%\AlienControl` (see `Services/Startup.cs`). Still to verify on the laptop with the What to test steps.
- Everything in beta1.4 is untested on the laptop: powrprof CPU cap path, NVML close / reopen on battery and minimize,
  PDH GPU % and VRAM values, the WPF-UI removal (Settings page, toasts, tooltips), focus ring, short-screen scaling.
- **GPU sensors by mode (beta1.4)**: in Advanced Optimus / Ultimate the GPU temp is the AWCC WMI sensor 0x06 (board
  sensor by the RTX) and GPU USE the RTX's PDH 3D engines (tag NVIDIA). In iGPU only the AWCC 0x06 sensor is not the
  Intel GPU, so the temp comes from IGCL and GPU USE from the Intel LUID's 3D engines (tag INTEL). IGCL status: interop
  written from Intel's public `igcl_api.h` (API 1.1, struct sizes covered by tests) but never run on the laptop; whether
  the m15 R7's Intel driver exposes a GPU temperature sensor through IGCL is unknown. crash.log says which source was
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
   `SHA256SUMS` to a GitHub release whose body is the notes file. Mark 2.0 betas as pre-release.

## Working from the Hark sandbox
There are no git push credentials. Use the GitHub connector (`apicall github`): create a branch with
`git_ref_create`, upload each changed file with `contents_put` (base64, with the current blob sha when updating;
call apicall from Python for big files like MainWindow.xaml to stay under the argv limit), `contents_delete` removed
files, `pull_create` into `v2.0`, wait for the build workflow, `pull_merge` (squash), then `git_ref_create` the tag at
the squash commit. Local compile check: .NET 8 SDK in /tmp and `dotnet build -p:EnableWindowsTargeting=true`.

## Conventions
- Comments tag each change with its version, plus "(Amine)" when it was his call.
- No em or en dashes anywhere in the UI. Legend only, 440 x 820, every tab the same height.
- Ask Amine before anything unclear.
