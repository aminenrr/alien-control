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

**[⬇️ Download the latest release](https://github.com/aminenrr/alien-control/releases/latest)** · [Installation guide](#installation)

## Contents

- [Screenshots](#screenshots)
  - [2.0: Home, Tuning, Lights](#20-home-tuning-lights)
  - [Overlay and settings](#overlay-and-settings)
  - [Notifications (F1, F2, plug / unplug)](#notifications-f1-f2-plug--unplug)
- [Features](#features)
- [Idle cost](#idle-cost)
- [Documentation](#documentation)
- [How it works](#how-it-works)
- [Installation](#installation)
  - [1. Install the .NET 8 Desktop Runtime](#1-install-the-net-8-desktop-runtime)
  - [2. Download Alien Controls](#2-download-alien-controls)
  - [3. Add the two alienfx-tools CLIs](#3-add-the-two-alienfx-tools-clis)
  - [4. Turn off AWCC](#4-turn-off-awcc)
  - [5. Run it](#5-run-it)
  - [6. Check the hotkeys](#6-check-the-hotkeys)
  - [Updating](#updating)
  - [Uninstalling](#uninstalling)
  - [m15 R7 power modes (verified)](#m15-r7-power-modes-verified)
- [Themes and settings](#themes-and-settings)
  - [Tray profiles](#tray-profiles)
  - [GPU mode](#gpu-mode)
- [Build](#build)
- [Security & Privacy](#security--privacy)
- [Credits](#credits)
- [Disclaimer](#disclaimer)
- [AI disclosure](#ai-disclosure)
- [License](#license)
- [Status](#status)
- [Issues and support](#issues-and-support)

## Screenshots

### 2.0: Home, Tuning, Lights

| Home | Tuning | Lights |
|---|---|---|
| ![Home](docs/mockups/2.0/01-home.png) | ![Tuning](docs/mockups/2.0/02-tuning.png) | ![Lights](docs/mockups/2.0/03-lights.png) |

### Overlay and settings

| Overlay tab | Settings: General · Keys | Settings: Alerts · Advanced |
|---|---|---|
| ![Overlay](docs/mockups/2.0/04-overlay.png) | ![Settings 1](docs/mockups/2.0/05-settings-general-keys.png) | ![Settings 2](docs/mockups/2.0/06-settings-alerts-advanced.png) |

*2.0 images are the approved design mockups (values are samples); real screenshots will replace them.*

### Notifications (F1, F2, plug / unplug)

| F2 cycle | F1 G-Mode | Auto preset |
|---|---|---|
| ![F2 Quiet](docs/screenshots/toasts/quiet.png) | ![G-Mode on](docs/screenshots/toasts/gmode-on.png) | ![Unplugged](docs/screenshots/toasts/unplug.png) |
| ![F2 Balanced](docs/screenshots/toasts/balanced.png) | ![G-Mode off](docs/screenshots/toasts/gmode-off.png) | ![Plugged in](docs/screenshots/toasts/plug.png) |
| ![F2 Performance](docs/screenshots/toasts/performance.png) | | |

*Real screenshots from the m15 R7. The plug / unplug toasts are recreated from a real toast with the same layout.*

## Features

- **2.0 layout**: one 440 x 820 window with four tabs, **Home** (sensors, power, presets, G-Mode, lighting), **Tuning**
  (performance mode, GPU mode, GPU overclock, CPU boost, refresh rate, fans), **Lights** (zones, color, effect,
  brightness, backlight timeout, status keys) and **Overlay**. **F3** opens / hides it, games included
  (borderless). The compact view of 1.1 to 1.3 is gone.
- **In-game overlay** (2.0): frametimes from PresentMon (no injection, click through), 4 levels (1 horizontal strip, 2 Metal HUD style, 3 and 4 MangoHud style) plus Advanced, latency toggle, per game FPS cap and V-Sync through the NVIDIA driver profile. Off = nothing runs.
  Move works like the NVIDIA overlay (2.0.1): both steppers count from the chosen corner toward the screen centre. The
  Metal level reads G-SYNC On / Off from the NVIDIA driver when the RTX drives the screen.
- Presets from the tray, the header preset button, the window or **F2**: Battery, Quiet, Balanced, Performance (power mode, CPU boost, refresh rate), plus Custom.
- **F1 G-Mode**: fans max, Aggressive CPU boost, optional GPU overclock (core / memory offsets, AC only, keep-or-revert test).
  Since 2.0.2 an OC test that is reverted, times out or fails only counts as done once the GPU reads +0 again; if the
  app quits or crashes first (or the RTX is off), it puts the GPU back to +0 on the next launch.
- **GPU OC in Performance** (v1.3.4): the **PERF / G-MODE** pill in the GPU Overclock header (2.0; a toggle with the
  Performance icon before; PERF lights red and G-MODE white since 2.0.1). **PERF** = your kept offsets also run in the
  Performance preset on AC. **G-MODE** (default) = G-Mode only. The Performance and G-Mode on toasts show **OC +core / +mem** in red when
  it runs, or a grey **OC off**.
- **Auto preset**: Battery when unplugged, your last preset when plugged back in.
- Fans, temps, **CPU % / GPU %** (v1.3) and CPU / GPU / total power readout while the window is open. Temps and fans come straight from the AWCC WMI interface (v1.1, ~2.5x faster than the CLI, CLI fallback).
- Per-key keyboard color and effects, chassis lights, status keys (G-Mode, mutes, touchpad) lit by the app.
- **Zones** (v1.3.1, inline on the Lights tab since 2.0): tiles for the keyboard, the rear ring and the Alienhead (lit = on,
  tap to switch) plus Turn off / Turn on. Ctrl + Alt + F cycles the light level of all three (full, off, 50%). The power button light is never touched.
- **Keyboard light timeout** (v1.3): fades the keys out after 1 / 2 / 5 / 10 min without input (optionally on battery
  only, paused in fullscreen games); any key, mouse or touchpad input brings them straight back.
- GPU mode: Advanced Optimus / Ultimate through the Dell BIOS switch (restart prompt), plus **iGPU only** (v1.2): turns the RTX off for battery life when NVIDIA is set to Optimus. It lists the apps using the RTX first, and HDMI / Mini DP stay dark until you switch back.
- Toasts for F1, F2 and plug / unplug (v1.3): a **Pill** at the top center (default) or the 1.2 **Classic** card in the
  top right, with a chime per preset from the **Soft** (default) or **Classic** pack. Since v1.3.4 they also show the GPU OC state.
- Locked buttons (Battery under Auto preset, iGPU only outside Optimus) show a small lock; click a locked iGPU only to see why.
- Legend look (the Fluent theme was removed in 2.0), Reduce motion, Show in taskbar (off = tray only), Reset settings. One exe, ~0% CPU and a few MB of RAM in the tray.

## Idle cost

Measured on my m15 R7 (v1.0.0, Task Manager):

| State | CPU | RAM |
|---|---|---|
| Window open, idle | 0% | ~20 MB |
| Window open, busy (switching, sensors) | brief spikes | ~40 MB |
| In the tray | 0% | a few MB |

How it stays that small:
- Sensors (temps, fans, watts) are polled only while the window is open on a tab that shows them (Home, Tuning); closed, nothing polls.
- Status keys (mute, mic, touchpad) update from Windows events (audio endpoint and registry callbacks), with a 30 s safety check.
- Memory is trimmed a minute after the window hides or a toast closes (a quick reopen stays snappy), and 10 s after starting to the tray.
- One framework-dependent ReadyToRun exe (faster start, no self-contained runtime). The release zip is ~5.7 MB (it bundles PresentMon for the overlay).
- No blur or Mica.

## Documentation

- [Hardware findings](docs/HARDWARE.md): everything measured on the m15 R7 (power map, light IDs, the status-key root cause)
- [Decisions log](docs/DECISIONS.md): why the app works the way it does
- [Project status](docs/STATUS.md): what's done, what's next, architecture and how to release (start here)
- [Development guide](docs/DEVELOPMENT.md): code layout, build, themes, mockups, workflow
- [Branches](docs/BRANCHES.md): where work happens and how a stable ships
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

## Installation

> [!WARNING]
> Made and tested only on the **Alienware m15 R7**. Read the caution box at the top first.

**You need:** Windows 10 / 11 (64-bit), admin rights, and about 5 minutes.
You do **not** need to install AWCC, the AlienFX app (`alienfx-gui`) or any driver. Alien Controls only uses two
small command-line tools from the alienfx-tools zip.

### 1. Install the .NET 8 Desktop Runtime

Download **.NET Desktop Runtime 8 (x64)** from [Microsoft](https://dotnet.microsoft.com/download/dotnet/8.0) and
install it. Skip this if you already have it.

### 2. Download Alien Controls

Get `AlienControl-<version>-win-x64.zip` from the
[Releases page](https://github.com/aminenrr/alien-control/releases/latest) and unzip it into **`C:\Program Files\Alien Controls`** (create the folder; Windows asks for admin).
The app runs as administrator, so its folder should only be changeable by admins: a folder like `C:\Tools` can be
written by any program, which could swap a file there and get admin rights. The app warns you if its folder is
writable without admin rights.

### 3. Add the two alienfx-tools CLIs

1. Open the [alienfx-tools releases](https://github.com/T-Troll/alienfx-tools/releases/latest) and download
   **`alienfx-tools.zip`** (not the `.msi` installer and not `fan-SDK_V1.zip`).
2. From that zip, copy only these two files into the same folder as `AlienControl.exe`:
   - `alienfan-cli.exe` (fans, power modes, G-Mode, CPU boost, temps)
   - `alienfx-cli.exe` (keyboard and chassis lighting)

   A `tools\` subfolder next to the exe works too. You don't need anything else from the zip.
3. Use alienfx-tools **9.6.x** (9.6.1 to 9.6.2.1). Since 1.3 the app checks the SHA-256 of both CLIs against T-Troll's
   published 9.6.x builds and refuses to run anything else (the hash goes to `crash.log` if it refuses).

Your folder should look like this:

```
C:\Program Files\Alien Controls\
  AlienControl.exe
  alienfan-cli.exe
  alienfx-cli.exe
```

### 4. Turn off AWCC

AWCC and Alien Controls fight over the same hardware, so turn AWCC off:

1. Press `Win + R`, type `services.msc`, press Enter.
2. For each **Alienware / AWCC** service: right-click, then **Properties**. Set **Startup type** to **Disabled**,
   click **Stop**, then **OK**.
3. Also close or disable AWCC in **Task Manager → Startup apps** if it's there.

You can undo this anytime by setting the services back to Automatic.

### 5. Run it

1. Double-click `AlienControl.exe` and accept the admin prompt (fan and power control need admin).
   Windows SmartScreen may warn because the exe isn't signed: click **More info → Run anyway**.
2. The window opens and the app lives in the tray (the Alienhead icon). The tabs at the top hold every setting.
3. Turn on **Run on startup** at the bottom of the window. It starts with Windows, in the tray, without an admin prompt.
   Because that start is elevated, the app first copies itself (the exe, the `tools` folder, LICENSE and README) to
   `C:\Program Files\AlienControl`, a folder only admins can change, and the startup task runs that copy. Your
   own folder (Downloads, Desktop) is never what starts as admin. Running a newer version from anywhere updates that
   copy. Turning **Run on startup** off deletes the task and removes the Program Files copy (if that copy is the one
   running, it goes at the next restart). Settings in `%AppData%\AlienControl` are never touched.

### 6. Check the hotkeys

- **F1** toggles G-Mode, **F2** cycles the presets, **F3** opens / hides the window. **F4** turns the overlay off / back to its last level, **Shift + F4** cycles the levels.
- If F1 does nothing, open the gear (next to Quit) > **General · Keys**, click **Detect key**, and press F1 once.

### Updating

Close the app from the tray (**Quit**), replace `AlienControl.exe` with the new one, and start it again.
Keep the two CLIs. Settings stay in `%AppData%\AlienControl`. With **Run on startup** on, starting the new version
once also updates the copy in `C:\Program Files\AlienControl` that runs at logon.

### Uninstalling

If **iGPU only** is on, switch back to Advanced Optimus first (or say *Turn it on* when Quit asks): the RTX stays
disabled after uninstalling otherwise, and `pnputil /enable-device` or Device Manager is the only way back. Quit also
removes the Quiet preset's 3.0 GHz CPU cap from your power plan (if you uninstall without quitting, run
`powercfg /setacvalueindex scheme_current sub_processor PROCFREQMAX 0` and the same with `setdcvalueindex`).

Turn off **Run on startup** (this also removes `C:\Program Files\AlienControl`), quit the app, and delete the folder (and `%AppData%\AlienControl` if you want the settings
gone). Set the AWCC services back to Automatic if you want AWCC again.

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

Since 2.0 there is one look, **Legend**: Alienware-style terminal look, mono numbers, icon section headers, tick bars,
cyan accent. The accent follows the lighting color. The Fluent theme was removed (an old "Theme" setting is ignored).
`Themes/Legend.xaml` (palette and icons) and `Themes/Legend2.xaml` (the 2.0 styles) are hand-edited. No blur or Mica
(a native Acrylic backdrop was tried in 1.1.0-beta.2 and dropped: it cost CPU and RAM for little gain), and the app
idles at ~0% CPU.

There are a few subtle, one-shot animations: the window slides up out of the bottom edge when shown and back down before
hiding to the tray, like the Windows 11 Quick Settings flyout; buttons fade their hover and shrink slightly when
pressed, a newly selected mode grows its underline and pulses once, the preset button fades to its new color. They
animate only opacity and scale/position, never loop and never run while the window is hidden. **Reduce motion**
(gear > General · Keys) turns all of them off.

The gear opens a **settings page** inside the window (back arrow or Esc), with two tabs: **General · Keys** (show in
taskbar, reduce motion, auto preset, Detect key for the G-key, F2 presets, F3 opens the app, F4 for the overlay) and
**Alerts · Advanced** (notifications, toast style + Test, sound, chime pack + preview, frame-gap log, **Reset
settings**: back to defaults and restart; startup, BIOS and GPU mode untouched, and the credits). Run on startup is in
the footer; the keyboard light timeout is on the Lights tab.

### Tray profiles

Left-click the tray icon to open the window, or to slide it back to the tray when it is open (v0.8.7).
Right-click it for the menu, drawn in the active theme with accent-colored text (v0.8.7): Battery (amber), Quiet (green), Balanced (cyan), Performance (red), Custom (violet); G-Mode shows white.
A profile sets the power mode, CPU boost and refresh rate: Battery (boost Off, 60 Hz), Quiet (Efficient, 240 Hz),
Balanced (On, 240 Hz), Performance (Aggressive, 240 Hz). Custom changes nothing. Profiles never touch the GPU mode and never use Ultimate.
The header has a preset button next to minimize (v1.1): the active preset's icon in its color. Since 2.0 it is hidden on
Home (the presets are on the page); a click cycles like F2 and a right click opens the list of presets. It follows tray picks, mode buttons, F2 and refresh rate changes.

### GPU mode

Three buttons: **iGPU only**, **Advanced Optimus** and **Ultimate**. The hint on the right shows what drives the screen
(Optimus · Iris Xe, Auto · RTX 3070 Ti, dGPU only · RTX 3070 Ti); change that from NVIDIA's own app.
Switching between Advanced Optimus and Ultimate sets the Dell BIOS graphics switch and asks to restart.
**iGPU only** (v1.2) turns the RTX off with `pnputil` for battery life. It only works in Advanced Optimus with NVIDIA set to
Optimus, lists the apps using the RTX first, and locks Ultimate while it's off. HDMI / Mini DP stay dark, and it
**survives restarts and uninstalling** until you click Advanced Optimus again (Quit offers to turn it back on).
Locked buttons show a small lock. Presets never change the GPU mode.

| GPU mode (1.3) |
|---|
| ![GPU mode](docs/screenshots/gpu-legend.png) |

## Build

```
dotnet publish src/AlienControl/AlienControl.csproj -c Release -r win-x64 -o publish
```

Publishing gives one ReadyToRun `AlienControl.exe` (needs the .NET 8 Desktop Runtime) plus `tools/`.

Every push to `main` also builds on GitHub Actions, runs the unit tests (`tests/AlienControl.Tests`) and uploads the
app as an artifact. Actions are pinned to commit SHAs, and each release has a `SHA256SUMS` file next to the zip.

Stack: C# · .NET 8 · WPF, no third-party UI library (WPF-UI was removed in 2.0.0-beta1.4).

## Security & Privacy

**Alien Controls does nothing on the network.** It has no HTTP client, no sockets, no update check, no telemetry,
analytics or crash reporting, no accounts and no listening ports. Nothing leaves your PC.

What it does locally, and why:
- **Runs as administrator.** It needs admin for the AWCC / Dell BIOS WMI calls, the keyboard HID, `pnputil` (iGPU only)
  and the startup task. Nothing else asks for more rights than that. The startup task (registered through the Task
  Scheduler API, no temp file) only ever starts the admin-only copy in `C:\Program Files\AlienControl` (2.0.0-beta1.4).
- **Runs only fixed programs**: the two alienfx-tools CLIs next to the exe, plus Windows' own `pnputil`, `powercfg`,
  `shutdown` and `net` (only if NVIDIA's Display Container has dependent services when it is restarted after iGPU only;
  the restart itself goes through the Windows service manager), always called by their
  full System32 path, never looked up through PATH. Arguments are built by the app, never taken from outside input.
- **Checks what it runs** (v1.3): the two CLIs must match T-Troll's published 9.6.x SHA-256 hashes, and are held
  open (no writes) while they run; `nvml.dll` is loaded only from System32. Settings are clamped to safe values on load.
- **Stores** only its settings (written atomically, with a `.bak`) and a local log in `%AppData%\AlienControl` (no secrets, no personal data). There are no keys, tokens
  or passwords in the code.
- **Reads** sensors, BIOS attributes, adapter state and the GPU memory of running apps (for the "RTX in use" list)
  locally; none of it is saved or sent anywhere.

If a network feature is ever added (for example an opt-in update check), it will be off by default, HTTPS only
(TLS 1.2+, certificate validation on), use one fixed GitHub endpoint, parse the reply defensively, send nothing about
you, and be listed here.

The only network traffic is yours: downloading the release from GitHub and the alienfx-tools CLIs from T-Troll's repo.

## Credits

All hardware control is done by [alienfx-tools](https://github.com/T-Troll/alienfx-tools)
by T-Troll and contributors (MIT license).
Icons: [Tabler Icons](https://tabler.io/icons) (MIT license).
The overlay uses [PresentMon](https://github.com/GameTechDev/PresentMon) (Intel, MIT) for frametimes.
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
(see [docs/archive/RC-CHECKLIST.md](docs/archive/RC-CHECKLIST.md)).

The AI (LLM) tools were used responsibly:

- **Verified on real hardware.** No hardware value was guessed: power modes, light IDs, key codes and OC limits were
  measured with probe scripts on the laptop before any code used them.
- **Reviewed, not blindly pasted.** Every change was read, built and tested by me before it was committed.
- **Small, safe steps.** Each feature shipped as its own small version and was tested before moving on, with risky
  parts (GPU mode, overclocking) behind confirmations and safe limits.
- **No reverse engineering of private code.** Only public interfaces and the open-source alienfx-tools were used.
- **Credited and open.** All code is public under MIT so anyone can check it, and third-party work is credited.

AI written code can contain mistakes, so please read the code before running it, especially because the app runs as
administrator and talks to the BIOS, the fans and the GPU.

## License

[MIT](LICENSE). Bundled alienfx-tools binaries and icon sets keep their own MIT licenses (see Credits).

## Status

**2.0.2** (stable, 2026-10-26): stability fixes on top of 2.0.1 (waking in G-Mode, hung hardware commands, quitting
during an overclock change, double GPU mode switches, OC test recovery). 2.0.1 brought NVIDIA style overlay Move,
the PERF / G-MODE pill colours and Metal HUD polish. 2.0.0 (2026-10-25) was the 2.0 look (tabs, Legend only, no
compact view) plus the in-game overlay (PresentMon frametimes, 4 levels, level 2 in the Apple Metal HUD style, per
game FPS cap). Download it from the
[Releases page](https://github.com/aminenrr/alien-control/releases/latest). The previous stable, **v1.3.5**, stays on
the `v1.3` branch. Full history in the [CHANGELOG](docs/CHANGELOG.md), next ideas in the [Roadmap](docs/ROADMAP.md),
current state in [docs/STATUS.md](docs/STATUS.md).

## Issues and support

There is no support for other models, and issues or requests may go unanswered: this is a personal project.
Bug reports for the m15 R7 are welcome. Forks and pull requests that take the idea further are even more welcome.
