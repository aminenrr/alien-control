# Alien Control

A lightweight, G-Helper-style control panel for the **Alienware m15 R7**, built on top of
[T-Troll's AlienFX Tools](https://github.com/T-Troll/alienfx-tools). It replaces Alienware
Command Center, whose background services keep *WMI Provider Host* busy.

![mock](mock/mock.jpg)

## How it works

Alien Control is GUI only. Every button runs one of T-Troll's command-line tools:

| Feature | Tool | Command |
|---|---|---|
| Performance mode | alienfan-cli | `setpower=<n>`, `getpower` |
| G-Mode | alienfan-cli | `setgmode=1/0`, `gmode` |
| GPU power limit (unverified on m15 R7) | alienfan-cli | `setgpu=0..4` |
| CPU boost | alienfan-cli | `setperf=<ac>,<dc>` |
| Fans | alienfan-cli | `setfans=<boost>,<boost>` |
| Temps / RPM | alienfan-cli | `temp rpm` |
| Lighting | alienfx-cli | `setall`, `setdim`, `setglobal` |

Sensors are polled only while the window is open; closed, it idles in the tray.

## Setup

1. Stop and disable the AWCC services (`services.msc`).
2. Download the latest [alienfx-tools release](https://github.com/T-Troll/alienfx-tools/releases),
   copy `alienfan-cli.exe`, `alienfx-cli.exe` (plus their DLLs) into `tools/` next to `AlienControl.exe`.
   On Intel systems also copy the `DPTF` files as described in T-Troll's README.
3. Run `alienfx-cli probe` once so your lights are named.
4. Run `alienfan-cli` (as admin) with no arguments to see your power modes, then set the
   indexes in `%AppData%\AlienControl\settings.json` (`QuietPowerIndex`, `BalancedPowerIndex`, `PerformancePowerIndex`).

## Build

```
dotnet publish src/AlienControl/AlienControl.csproj -c Release -r win-x64 --self-contained false -o publish
```

Every push to `main` also builds on GitHub Actions and uploads the app as an artifact.

Stack: C# · .NET 8 · WPF · [WPF-UI](https://github.com/lepoco/wpfui) (Fluent look).

## Credits

All hardware control is done by [alienfx-tools](https://github.com/T-Troll/alienfx-tools)
by T-Troll and contributors (MIT license).

## Status

v0.1 skeleton: UI wired to both CLIs. Not yet tested on hardware.
