# Alien Controls

> [!CAUTION]
> ## 🚨 Read this first
> **This is a personal project, purpose-built for my own Alienware m15 R7.** There are no plans to support other laptops
> for now. It drives fans, power modes, the BIOS GPU switch and GPU clocks directly, so on any other model it may do the
> wrong thing.
>
> **I am not responsible for any damage to your laptop, your data or anything else. You use it entirely at your own risk.**
> **Read the code and understand what it does before you run it.**
>
> Better yet, treat it as a **proof of concept**: a starting point for more capable developers to build a proper
> G-Helper-style tool for Alienware / Dell laptops. Unofficial, not affiliated with Dell or Alienware, and built with the
> help of AI. See [Disclaimer](#disclaimer) and [AI disclosure](#ai-disclosure).

A lightweight, G-Helper-style control panel for the **Alienware m15 R7**, built on top of
[T-Troll's AlienFX Tools](https://github.com/T-Troll/alienfx-tools). It replaces Alienware
Command Center, whose background services keep *WMI Provider Host* busy.

| Legend theme | Fluent theme |
|---|---|
| ![Legend theme](docs/screenshots/legend.png) | ![Fluent theme](docs/screenshots/fluent.png) |

### Notifications (F1, F2, plug / unplug)

| F2 cycle | F1 G-Mode | Auto preset |
|---|---|---|
| ![F2 Quiet](docs/screenshots/toasts/quiet.png) | ![G-Mode on](docs/screenshots/toasts/gmode-on.png) | ![Unplugged](docs/screenshots/toasts/unplug.png) |
| ![F2 Balanced](docs/screenshots/toasts/balanced.png) | ![G-Mode off](docs/screenshots/toasts/gmode-off.png) | ![Plugged in](docs/screenshots/toasts/plug.png) |
| ![F2 Performance](docs/screenshots/toasts/performance.png) | | |

*Real screenshots from the m15 R7 (Legend theme). The plug / unplug toasts are recreated from a real toast with the same layout.*

## Features

- **F3** opens / hides the app from anywhere, games included (borderless).
- Presets from the tray, the window or **F2**: Battery, Quiet, Balanced, Performance (power mode, CPU boost, refresh rate), plus Custom.
- **F1 G-Mode**: fans max, Aggressive CPU boost, optional GPU overclock (core / memory offsets, AC only, keep-or-revert test).
- **Auto preset**: Battery when unplugged, your last preset when plugged back in.
- Fans, temps and CPU / GPU / total power readout while the window is open.
- Per-key keyboard color and effects, chassis lights, status keys (G-Mode, mutes, touchpad) lit by the app.
- GPU mode: Advanced Optimus / Ultimate through the Dell BIOS switch (restart prompt).
- Small top-right toasts with soft sounds for F1, F2 and plug / unplug.
- Two themes (Legend, Fluent), Reduce motion, Reset settings. One exe, ~0% CPU and a few MB of RAM in the tray.

## Documentation

- [Hardware findings](docs/HARDWARE.md): everything measured on the m15 R7 (power map, light IDs, the status-key root cause)
- [Decisions log](docs/DECISIONS.md): why the app works the way it does
- [Development guide](docs/DEVELOPMENT.md): code layout, build, theme generator, mockups, workflow
- [Changelog](docs/CHANGELOG.md) and [Roadmap](docs/ROADMAP.md)

## How it works

Alien Control is GUI only. Every button runs one of T-Troll's command-line tools:

| Feature | Tool | Command |
|---|---|---|
| Performance mode | alienfan-cli | `setpower=<n>`, `getpower` |
| G-Mode | alienfan-cli | `setgmode=1/0`, `gmode` |
| CPU boost | alienfan-cli | `setperf=<ac>,<dc>` |
| Fans | alienfan-cli | `setfans=<boost>,<boost>` |
| Temps / RPM | alienfan-cli | `temp rpm` |
| Lighting (per-key keyboard + chassis) | alienfx-cli | `setone` on every light ID, `setdim`, `setglobal` |
| G-key (F1) | app | global keyboard hook (always on), toggles `setgmode` |
| Refresh rate 60 / 240 Hz | app | `EnumDisplaySettings` / `ChangeDisplaySettingsEx` on the primary display (resolution kept) |
| Power readout (every 1 s) | app | CPU: `\EsifDeviceInformation(*)\RAPL Power` (Intel DTT) · GPU: NVIDIA NVML (`nvml.dll`, plugged in only) · Total: battery discharge rate on battery; on AC an estimate `~N W` = CPU + GPU + rest of the laptop (learned on battery) |
| GPU mode | app | Dell BIOS graphics switch (Advanced Optimus / Ultimate) and NVIDIA's display mode for the hint |

Sensors are polled only while the window is open; closed, it idles in the tray.

## Setup

1. Stop and disable the AWCC services (`services.msc`).
2. Download the latest [alienfx-tools release](https://github.com/T-Troll/alienfx-tools/releases),
   copy `alienfan-cli.exe` and `alienfx-cli.exe` (plus any DLLs they need) next to `AlienControl.exe`
   (a `tools/` folder there works too).
   On Intel systems also copy the `DPTF` files as described in T-Troll's README.
3. Run Alien Control as administrator. Defaults are set for the m15 R7; other models can edit
   `%AppData%\AlienControl\settings.json`.
4. Open the gear menu (next to Quit), click **Detect key** and press the G-key (F1) once so the app learns its key code.

### m15 R7 power modes (verified)

| `setpower=` | `getpower` raw | Mode |
|---|---|---|
| 1 | a0 | Balanced |
| 2 | a1 | Balanced-Performance |
| 3 | a5 | Battery Saver |
| 4 | a3 | Quiet |
| 5 | a4 | Performance |

`setgpu` is not in alienfan-cli v9.6, so there is no GPU power limit control.
The keyboard is per-key RGB (APIv5) and its light IDs don't follow key order, so colors are
sent to IDs 0 to 135. Any software color takes the keyboard out of firmware mode, which stops it
lighting the status keys, so the app drives them itself: F1 (G-Mode) = 1, F12 (touchpad lock) = 12,
speaker mute = 16, mic mute = 19, right Windows lock = 109. They turn white when on.
The power-button Alienhead is never touched. Avoid `alienfx-cli probe` on the keyboard: interrupting it
leaves keys dark until a reboot.

## Themes and settings

Two themes, switched in the gear menu (saved in settings, default **Legend**):

- **Legend**: Alienware-style terminal look, mono numbers and icon section headers, tick bars, cyan accent.
- **Fluent**: Windows 11 cards, segmented controls and rounded bars.

Each theme is its own ResourceDictionary (`Themes/Legend.xaml`, `Themes/Fluent.xaml`); only the active one is loaded. No blur or Mica, and the app idles at ~0% CPU.
The accent follows the lighting color.

Since v0.8.2 there are a few subtle, one-shot animations: since v0.8.6 the window slides up out of the bottom edge when shown
and back down before hiding to the tray, like the Windows 11 Quick Settings flyout; buttons fade their hover and shrink slightly when pressed, a newly selected mode grows its
underline and pulses once, the profile chip fades to its new color, and the gear menu slides up. They animate
only opacity and scale/position, are capped at 60 fps, never loop and never run while the window is hidden.
**Reduce motion** in the gear menu turns all of them off (saved in settings, off by default).

The gear menu also holds **Detect key** for the G-key, **Reduce motion**, F2 / auto preset / notification toggles,
**Reset settings** (back to defaults and restart; startup, BIOS and GPU mode untouched) and the version / credits line.

### Tray profiles

Left-click the tray icon to open the window, or to slide it back to the tray when it is open (v0.8.7).
Right-click it for the menu, drawn in the active theme with accent-colored text (v0.8.7): Battery (amber), Quiet (green), Balanced (cyan), Performance (red), Custom (violet); G-Mode shows white.
A profile sets the power mode, CPU boost and refresh rate: Battery (boost Off, 60 Hz), Quiet (Efficient, 240 Hz),
Balanced (On, 240 Hz), Performance (Aggressive, 240 Hz). Custom changes nothing. Profiles never touch the GPU mode and never use Ultimate.
The header shows the current profile as a small read-only chip next to the model badge ("● BALANCED" in Legend,
"● Balanced" in Fluent), tinted like the tray icon. It follows tray picks, mode buttons and refresh rate changes.

### GPU mode

Two buttons: **Advanced Optimus** and **Ultimate**. The hint on the right shows what drives the screen
(Optimus · Iris Xe, Auto · RTX 3070 Ti, dGPU only · RTX 3070 Ti); change that from NVIDIA's own app.
Switching between the two sets the Dell BIOS graphics switch and asks to restart. Presets never change it.

| Legend | Fluent |
|---|---|
| ![GPU mode, Legend](docs/screenshots/gpu-legend.png) | ![GPU mode, Fluent](docs/screenshots/gpu-fluent.png) |

## Build

```
dotnet publish src/AlienControl/AlienControl.csproj -c Release -r win-x64 -o publish
```

Publishing gives one ReadyToRun `AlienControl.exe` (needs the .NET 8 Desktop Runtime) plus `tools/`.

Every push to `main` also builds on GitHub Actions and uploads the app as an artifact.

Stack: C# · .NET 8 · WPF · [WPF-UI](https://github.com/lepoco/wpfui) (Fluent look).

## Credits

All hardware control is done by [alienfx-tools](https://github.com/T-Troll/alienfx-tools)
by T-Troll and contributors (MIT license).
Legend theme icons: [Tabler Icons](https://tabler.io/icons) (MIT license). Fluent theme icons: Fluent System Icons via
[WPF-UI](https://github.com/lepoco/wpfui) (MIT), fan icon from [Phosphor](https://phosphoricons.com) (MIT).
RAM clean idea from [WinMemoryCleaner](https://github.com/IgorMundstein/WinMemoryCleaner) and GPU modes from [G-Helper](https://github.com/seerge/g-helper). Both ideas are reimplemented here, and no code was copied.

## Disclaimer

This project is not affiliated with or endorsed by Dell, Alienware, NVIDIA, Intel or Microsoft, or by the alienfx-tools
developers. It contains no Dell or Alienware software: the hardware is driven by T-Troll's alienfx-tools, which you
download yourself from their release page.

The app changes low-level settings: power modes and fans through the embedded controller, CPU boost, the Dell BIOS
graphics switch and NVIDIA GPU clock offsets. It was tested only on one Alienware m15 R7 (i7-12700H, RTX 3070 Ti
Laptop); other models or BIOS versions may behave differently or not work. GPU overclocking can make games or the
system unstable. Use it at your own risk.

Alienware is a trademark of Dell Inc. NVIDIA, GeForce and RTX are trademarks of NVIDIA Corporation. Intel is a
trademark of Intel Corporation. Windows is a trademark of Microsoft. They are named here only to describe what the
app works with.

## AI disclosure

This project was built with the help of AI (Hark). AI was used to read alienfx-tools and Dell's interfaces to find how
to control the laptop, to work out the hardware details in [docs/HARDWARE.md](docs/HARDWARE.md) together with probe
scripts I ran on the laptop (power mode map, light IDs, the macro keys, the GPU overclock limits), and to help write and
debug the code, the theme generator, the docs and the design mockups. The README screenshots are real, except the
plug / unplug toasts, which were recreated from a real toast. I tested every feature myself on my own laptop
(see [docs/RC-CHECKLIST.md](docs/RC-CHECKLIST.md)).

AI written code can contain mistakes, so please read the code before running it, especially because the app runs as
administrator and talks to the BIOS, the fans and the GPU.

## License

[MIT](LICENSE). Bundled alienfx-tools binaries and icon sets keep their own MIT licenses (see Credits).

## Status

**v1.0.0: first stable release**, tested on my m15 R7. Download it from the
[Releases page](https://github.com/aminenrr/alien-control/releases/latest). Full history in the
[CHANGELOG](docs/CHANGELOG.md), next ideas in the [Roadmap](docs/ROADMAP.md).

## Issues and support

There is no support for other models, and issues or requests may go unanswered: this is a personal project.
Bug reports for the m15 R7 are welcome. Forks and pull requests that take the idea further are even more welcome.
