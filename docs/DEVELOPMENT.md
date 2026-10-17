# Development guide

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
| `App.xaml(.cs)` | tray icon, theme loading (`ApplyTheme`), accent (`ApplyAccent`), single window |
| `MainWindow.xaml(.cs)` | the card UI, sensor loop (only while visible), hints, preset button, gear flyout |
| `MainWindow.Compact.cs` | v1.1 compact / full view (`SetCompact`), v1.2 card-swap `SwitchView`, footer view button, keeps the corner anchored on resize |
| `Services/SysExe.cs` | v1.2 full System32 paths for the Windows tools the app runs |
| `MainWindow.Presets.cs` | v1.1 header preset button and its preset list popup |
| `Services/AwccSensors.cs` | v1.1 direct WMI temps and fan RPM (`AWCCWmiMethodFunction.Thermal_Information`), CLI fallback in `AlienFan` |
| `Motion.cs` | all animations (v0.8.2, values and spline curves updated in v0.8.6), disabled by Reduce motion. The window slide is native since v0.8.8: `MainWindow.xaml.cs` (`PrepareShow` / `PlayOpen` / `HideToTray`) drives `Services/WindowSlide.cs` |
| `Services/WindowSlide.cs` | v0.8.8 native window slide: `SetWindowPos` per frame (Rendering + 15 ms backup timer, only while sliding), cubic-bezier curves |
| `Themes/Legend.xaml`, `Themes/Fluent.xaml` | **generated** by `dev/gen_themes.py`; same keys in both. Edit the generator (keep any hand edit to the XAML mirrored there) |
| `Services/CliRunner.cs` | runs the T-Troll CLIs hidden and captures output |
| `Services/AlienFan.cs` | power modes, G-Mode, CPU boost, fans, temps (parses raw `getpower` hex) |
| `Services/AlienFx.cs` | chassis lighting through the CLI; filters the power button (ID 2) |
| `Services/KeyboardHid.cs` | whole-keyboard HID frames (136 keys in one report), software brightness, animations |
| `Services/GKeyHook.cs` | low-level keyboard hook for the F1 G-key (VK 0x80) |
| `Services/SystemState.cs` | mute / mic / touchpad-lock state for the status keys |
| `Services/SystemStats.cs` | RAM (GlobalMemoryStatusEx) and VRAM (GPU Adapter Memory counters) |
| `Services/MemoryCleaner.cs` | quick / deep RAM clean (NtSetSystemInformation memory lists, file cache) |
| `Services/PowerMeter.cs` | CPU RAPL counter, NVIDIA NVML, battery discharge |
| `Services/DisplayRefresh.cs` | 60 / 240 Hz on the primary display |
| `Services/DellBios.cs` | Dell BIOS WMI attributes (ExpressCharge, MUX lookup) |
| `Services/GpuMode.cs` | GPU mode detection, adapter disable/enable, apps using the RTX, support report. Buttons not wired yet |
| `Services/Profiles.cs` | tray profiles and their tint colors |
| `Services/TrayMenu.cs` | v0.8.7 themed tray menu: Legend / Fluent palettes in C# (WinForms can't read the XAML), accent text, tint dots |
| `Services/Settings.cs` | `settings.json` with versioned migrations (currently v6) |
| `Services/Startup.cs` | run-at-logon scheduled task (admin, no UAC prompt) |

## Themes
`dev/gen_themes.py` writes both theme dictionaries. Icons come from SVG files (Tabler for Legend, Phosphor fan) that it
turns into XAML `Path` geometry; Fluent uses WPF-UI `SymbolIcon` names. Set `ROOT` / icon paths at the top before running.

## Mockups
`dev/mockups/` holds the Python scripts that render the design mocks as HTML and screenshot them with headless Chromium
(Playwright) at 1.25x to match the laptop's 125% scaling. `designs.py` holds the three original directions, `themes_final.py`
the approved themes, `profile_chip.py` the v0.8.1 chip and `gpu_warning.py` the planned RTX-in-use warning.
They need the icon SVGs and fonts (Selawik, Cascadia Mono / JetBrains Mono) locally.

## Rules
- Never send light ID 2 on the chassis (power button).
- Never write the keyboard with per-key `setone`; use `KeyboardHid` frames.
- Never let a profile change GPU mode or trigger a restart.
- Keep idle cost at zero: no timers while hidden, no looping animations.
- Credit every third-party asset in the README; never copy GPL code.

## Releases

Tag `vX.Y.Z` and push it: `.github/workflows/release.yml` builds the zip and publishes a GitHub Release (tags with `-` become pre-releases).
The release text comes from `docs/releases/<tag>.md`; start from `docs/releases/TEMPLATE.md`, which carries the standard disclaimer / AI line.
