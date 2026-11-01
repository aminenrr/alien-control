# Development guide

Start with [STATUS.md](STATUS.md) for what is done and what is next.

How the app is built, how the code is laid out, and how each change was made and tested.

## Workflow we use
1. Plan the change (see ROADMAP.md) and get Amine's OK.
2. For anything visual, render a mockup first (`dev/mockups/`), send it, adjust, then code it.
3. Write the code, compile-check it locally with the .NET 8 SDK, then push to `main`.
4. GitHub Actions builds it on Windows (`.github/workflows/build.yml`) and publishes the `AlienControl-win-x64` artifact.
5. Amine tests on the m15 R7 and sends screenshots / CLI output; findings go into HARDWARE.md and the next fix.

When a hardware behavior is unclear, we test it straight from `cmd.exe` with alienfan-cli / alienfx-cli first, one variable
at a time, and only then write code (that's how the status-key bug was found; see HARDWARE.md).

## Build
```
dotnet build src/AlienControl/AlienControl.csproj -c Release
```
Needs the .NET 8 SDK on Windows (or `EnableWindowsTargeting` to compile-check on Linux). Put T-Troll's `alienfan-cli.exe`,
`alienfx-cli.exe` and their DLLs in `src/AlienControl/tools/` (not committed). The app needs Administrator rights (app.manifest).

## Code layout
| File | Role |
|---|---|
| `App.xaml(.cs)` | tray icon, theme loading (`ApplyTheme`: Legend.xaml + Legend2.xaml), F3 toggle (`ToggleMain`), single window |
| `MainWindow.xaml(.cs)` | 2.0: the fixed 440 x 820 card (header 52, tabs 40, page, status 38, footer 52), the four tab pages and the settings page markup, sensor loop, hints, accent |
| `MainWindow.Tabs.cs` | 2.0: tab switching, polling only for the tab on screen (`SensorsShown` / `UpdatePolling`), Home preset tiles and G-Mode switch, F4 hint (overlay in beta 2) |
| `Services/SysExe.cs` | v1.2 full System32 paths for the Windows tools the app runs |
| `MainWindow.Settings.cs` | settings page (gear): 2.0 two tabs (General · Keys, Alerts · Advanced), back / Esc, dimmed dependent rows, chime pack, toast style, frame-gap log |
| `Services/Sounds.cs` | 1.3.0-beta.2 chime packs (`Assets/Sounds/{Soft,Classic}`), only the active pack cached |
| `Services/Toast.cs` | toasts: 1.3.0-beta.2 Pill (top center) or Classic (1.2 card, top right); 1.3.4 optional OC tag (`Info.Oc` / `OcLit`) |
| `MainWindow.Oc.cs` | GPU overclock: NVML offsets, keep-or-revert test, G-Mode / Performance scope (`OcInPerformance`, 2.0 G-MODE / + PERF pill), OC toast tag |
| `MainWindow.Window.cs`, `.Sensors.cs`, `.Modes.cs`, `.Gpu.cs` | 2.0.0-beta1.4 split: placement / scaling / slide / AllowClose, sensor reads, command gate and power modes, GPU mode row. See [STATUS.md](STATUS.md) |
| `MainWindow.Presets.cs` | header preset button (2.0: hidden on Home, click cycles like F2, right click = list popup) |
| `Services/AwccSensors.cs` | v1.1 direct WMI temps and fan RPM (`AWCCWmiMethodFunction.Thermal_Information`), CLI fallback in `AlienFan` |
| `Motion.cs` | all animations (v0.8.2, values and spline curves updated in v0.8.6), disabled by Reduce motion. The window slide is native since v0.8.8: `MainWindow.xaml.cs` (`PrepareShow` / `PlayOpen` / `HideToTray`) drives `Services/WindowSlide.cs` |
| `Services/WindowSlide.cs` | native window slide: since 1.3 a short-lived thread steps it once per composition (DwmFlush) with async `SetWindowPos`, cubic-bezier curves |
| `Themes/Legend.xaml` | base palette and Tabler icon geometry, hand-edited since 2.0.0-beta1.4 (generator retired to `dev/retired/`). Fluent was removed in 2.0 |
| `Themes/Legend2.xaml` | 2.0 styles and icons (tabs, tiles, pills, toggles, cards), hand written, merged after Legend.xaml |
| `Services/CliRunner.cs` | runs the T-Troll CLIs hidden and captures output |
| `Services/AlienFan.cs` | power modes, G-Mode, CPU boost, fans, temps (parses raw `getpower` hex) |
| `Services/AlienFx.cs` | chassis lighting through the CLI; filters the power button (ID 2) |
| `Services/KeyboardHid.cs` | whole-keyboard HID frames (136 keys in one report), software brightness, animations |
| `Services/GKeyHook.cs` | low-level keyboard hook for the F1 G-key (VK 0x80) |
| `Services/SystemState.cs` | mute / mic / touchpad-lock state for the status keys |
| `Services/SystemStats.cs` | RAM (GlobalMemoryStatusEx), VRAM and GPU % through one PDH query |
| `Services/MemoryCleaner.cs` | quick / deep RAM clean (NtSetSystemInformation memory lists, file cache) |
| `Services/PowerMeter.cs` | CPU RAPL counter, NVIDIA power through NvmlSession (closed on battery and when not polling), battery discharge |
| `Services/DisplayRefresh.cs` | 60 / 240 Hz on the internal laptop panel (primary only with the lid closed) |
| `Services/DellBios.cs` | Dell BIOS WMI attributes (MUX lookup) |
| `Services/GpuMode.cs` | GPU mode detection, RTX disable / enable through pnputil |
| `Services/Profiles.cs` | tray profiles and their tint colors |
| `Services/TrayMenu.cs` | v0.8.7 themed tray menu: palettes in C# (WinForms can't read the XAML), accent text, tint dots. 2.0 always passes Legend |
| `Services/Settings.cs` | `settings.json` with versioned migrations (v7), atomic save with retry and pending flush, `.corrupt` rename, unknown keys kept |
| `Services/Startup.cs` | run-at-logon scheduled task (admin, no UAC prompt) |

## Themes
Both theme files are hand-edited since 2.0.0-beta1.4: `Themes/Legend.xaml` (palette, Tabler icon `Path` geometry) and
`Themes/Legend2.xaml` (2.0 styles, including FocusRing2, ToolTip, TextBox and ScrollBar since WPF-UI was removed).
`App.ApplyTheme` merges both. Run `python dev/check_xaml_keys.py` after any edit (unreachable, missing and duplicate
keys; CI runs it too). The old generator is kept for reference in `dev/retired/gen_themes.py`. Fluent was removed in 2.0.

## Mockups
2.0 design: `docs/mockups/2.0/` (PNGs + `HANDOFF.md`, the spec the 2.0 UI follows; sizes and layout math came from
the Python mockup scripts kept in the project workspace).

`dev/mockups/` holds the Python scripts that render the design mocks as HTML and screenshot them with headless Chromium
(Playwright) at 1.25x to match the laptop's 125% scaling. `designs.py` holds the three original directions, `themes_final.py`
the approved themes, `profile_chip.py` the v0.8.1 chip and `gpu_warning.py` the planned RTX-in-use warning.
They need the icon SVGs and fonts (Selawik, Cascadia Mono / JetBrains Mono) locally.

## Rules
- Never send light ID 2 on the chassis (power button).
- Never write the keyboard with per-key `setone`; use `KeyboardHid` frames.
- Never let a profile change GPU mode or trigger a restart.
- Keep idle cost at zero: no timers while hidden, no looping animations. 2.0: only read what is on screen (sensor timers run for Home / Tuning only).
- Credit every third-party asset in the README; never copy GPL code.

## Releases

Tag `vX.Y.Z` and push it: `.github/workflows/release.yml` builds the zip and publishes a GitHub Release (tags with `-` become pre-releases).
The release text comes from `docs/releases/<tag>.md`; start from `docs/releases/TEMPLATE.md`, which carries the standard disclaimer / AI line.
