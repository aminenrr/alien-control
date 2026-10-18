# Decisions log

Why things are the way they are. Newest decisions win.

## Scope and stack (2026-10-14)
- **GUI only.** Every hardware action goes through T-Troll's alienfan-cli / alienfx-cli. The AlienFX core is never modified.
- **No fork** of alienfx-tools. Its MIT binaries are shipped in `src/AlienControl/tools/` (gitignored) with credit.
- C# .NET 8 + WPF + WPF-UI, tray icon, runs as admin. Goal: replace Alienware Command Center, whose services keep
  WMI Provider Host busy. AWCC services must be disabled first.
- Sensors are polled only while the window is open. The app idles in the tray.

## Lighting (2026-10-14/15)
- Never touch the power-button light (chassis ID 2). The firmware's AC blue / battery amber stays.
- Rear ring driven as one light (IDs 0 + 1), like AWCC. No per-segment mapping.
- Keyboard colors in one HID frame; brightness in software (see HARDWARE.md for why).
- The app paints the status keys white itself when their feature is on (firmware behavior emulated).
- Spectrum and Rainbow wave are software animations (~20 fps); the chassis uses its firmware spectrum.
- Done with colors after v0.6 (Amine, 2026-10-15).

## Naming and look
- Display name **Alien Controls** (v0.5). The exe stays `AlienControl.exe` so the startup task keeps working.
- Redesign (2026-10-15): three directions mocked (A Refined Nebula, B AWCC Terminal, C Fluent Mica). Amine liked B and C,
  so both ship as themes: **Legend** (B, Alienware style, Tabler icons) and **Fluent** (C, Windows 11 style,
  Fluent System Icons via WPF-UI + Phosphor fan). Switch in the gear menu. Only the active theme's dictionary is loaded.
- Animations (v0.8.2) replace the earlier "fully static" rule, on condition they cost nothing at idle:
  Opacity/RenderTransform only, one-shot, 60 fps, nothing while hidden. "Reduce motion" (off by default) makes it static.

- v0.8.6 (Amine): window open/close copies the Windows 11 Quick Settings flyout (slide up from the bottom edge, slide back
  down on close, no fade) instead of the v0.8.2 fade + 12 DIP rise. Hover/press/selection made stronger because the
  v0.8.2 values ran but were too faint to notice. Timings are by eye, tuned after the laptop test.

- v0.8.9 (Amine): profile CPU boost: Battery Off, Quiet Efficient, Balanced On, Performance Aggressive (same on AC and
  battery). Minimize slides down like hide and keeps the taskbar button.
- v0.8.8: animate the HWND, not the content. Amine's recording showed content animations inside the layered
  (`AllowsTransparency`) window are never presented mid-animation on this laptop. If hover/press fades turn out to be
  invisible too, the next step is dropping `AllowsTransparency` for a normal window with Windows 11 DWM corners and shadow.
- v0.8.7 (Amine): tray left click toggles the window. The tray menu follows the theme with accent-colored text; its
  palettes live in `TrayMenu.cs` because the WinForms menu can't use the WPF theme dictionaries.

- v0.8.4 (Amine): Legend section headers are icon-only (numbers removed); Fluent shows the brightness % once.

## Features
- RAM clean: Quick (click) and Deep (right-click). No auto-clean.
- G-key hook always on; its toggle was removed (Amine's choice).
- Battery mode = power index 3 (a5). Performance = index 2 (a1).
- Power readout from Windows/driver sources only (Intel DTT RAPL counter, NVIDIA NVML, battery discharge). No HWiNFO,
  LibreHardwareMonitor or WinRing0. GPU power isn't read on battery so the RTX can sleep.
- XMP dropped: stock RAM has no XMP profile.
- WinMemoryCleaner and G-Helper are GPL-3.0: their ideas are reimplemented, no code copied.

## v0.8.0 hardware test outcome (2026-10-15)
- Only the Battery preset drops to 60 Hz; the others stay at 240 Hz (Amine).
- Total power is shown on AC too, as an estimate: imperfect but better than nothing (Amine). Instead of a fixed +20-30 W,
  the rest-of-laptop draw is learned on battery where the total is real.
- Power readout refreshes every 1 s (Amine asked for faster).
- Faster charging removed for now, kept as a long-term goal.
- Big changes get a detailed written plan first, approved before any code (Amine, standing rule).

## Fans (2026-10-15)
- Custom fan curve dropped (Amine, after the AlienFX Discord): the BIOS/EC owns the curve on this laptop and overrides
  software; the app can only add boost on top. Fans row = Auto / Max Boost.

## GPU modes (2026-10-15)
- Row: **iGPU only / Optimus / dGPU only / Ultimate**. "Optimized" and the old Eco/Standard names were dropped.
  - iGPU only: disable the NVIDIA adapter (`pnputil`). Safe and reversible; external HDMI/USB-C outputs wired to the RTX go dark.
    Warn first, listing apps holding RTX memory (`GpuMode.AppsUsingDgpu()`).
  - Optimus: hybrid; the iGPU already drives the panel and the RTX sleeps when idle.
  - dGPU only: NVIDIA Advanced Optimus "NVIDIA GPU only" display mode (no reboot).
  - Ultimate: MUX through Dell BIOS WMI, manual only, restart prompt.
- **Rule:** tray profiles never switch to Ultimate and never touch GPU mode automatically.
- v0.9.2 (Amine): only **Advanced Optimus** and **Ultimate**. NVIDIA's Auto / Optimus / GPU only stay in its tray popup
  (no command opens it); the hint reads them. iGPU only (pnputil) dropped.
- v0.9.0 (Amine picked option A): NVIDIA's switch is a private driver call (Procmon), so no reverse engineering of
  nvapi64. The row reads the mode; Optimus / dGPU only open NVIDIA Control Panel. Mode detection uses the live panel
  path first (driver independent) and NVIDIA's registry flags only for Auto / fallback.
- The "RTX in use" warning will be an in-window overlay styled per theme (Cancel / Refresh / Switch anyway),
  not a plain Windows message box. Built when the buttons get wired.

## GPU overclock (2026-10-15)
- AWCC-like core / memory offsets, max +300 each, no MSI Afterburner. Amine runs +200 / +200, so that is the default.
- **G-Mode on AC only** (Amine's call, for safety). Leaving G-Mode, unplugging or quitting = +0.
- NVML `nvmlDeviceSetClockOffsets` (legacy VfOffset calls are Not Supported here). Memory offset counts double in NVML,
  so the UI shows AWCC MHz and sends memory x2.
- Safety: 20 s keep-or-revert after Apply, "pending" flag saved before the write so a crash comes back at +0.
  No stress test, no voltage curve, power limit or fan control. Profiles never change GPU mode.
- Layout (Amine): Core and Memory side by side, Keep / Revert replace the button row, to keep the window short.

## 1.1 (2026-10-16)
- Sensors: temps and fan RPM read straight from `AWCCWmiMethodFunction.Thermal_Information` (73 ms vs 181 ms for the CLI
  for four reads). alienfan-cli stays for every command and as the fallback after an init failure or three errors.
- Fluent Acrylic (Quick Settings blur) was built in beta.2 and **dropped** (Amine): it didn't look good enough and cost CPU and
  RAM. Not planned anymore.
- Compact view: the tray and F3 **always** open compact (no remembering the last view); F4 opens the full menu. The key for the
  view already shown hides it, the other key switches. Compact = header, sensors, power, performance mode, lighting colors + Off
  (no GPU mode). The version moved next to the status line so the footer center holds the Full menu / Compact button.
- Show in taskbar toggle, default on. Off = tray only; minimize then hides to the tray.
- The read-only header chip became a preset button (both themes): icon + color of the active preset, click for the list
  Battery, Quiet, Balanced, Performance, Custom.
- No rc for 1.1: each beta was confirmed on the laptop before the next one.

## iGPU only, revived (1.2.0-beta.2, 2026-10-17)
- Amine asked for it back as a third button: **iGPU only / Advanced Optimus / Ultimate**.
- Allowed only in Advanced Optimus with NVIDIA on Optimus (Intel on the panel). Automatic Select: pressable, warns to
  switch to Optimus first (it could hand the panel to the RTX). dGPU only / Ultimate: locked with a note.
- Confirm dialog lists apps holding RTX memory (Cancel / Refresh / Turn off anyway).
- Ultimate is locked while the RTX is off (never boot the MUX with the RTX disabled).
- Re-enable = Advanced Optimus only, no restart. Plugging in does not re-enable it (manual, default chosen while the
  question was open). NVIDIA's tray icon: restart NVDisplay.ContainerLocalSystem if nvcontainer isn't in the session.

## 1.2 (2026-10-17)
- Toasts copy the Dolby Controls card style; chimes are generated (dev/chimes.py) with a release fade + lead silence (no crackle).
- iGPU only: NVML is closed and the power timer paused before `pnputil` (reading a removed device crashed the app).
  Plugging in never re-enables the RTX on its own (manual).
- View switch = card swap (fully off screen, switch, rise): the 40 px dip showed the height jump.
- On open, alienfan-cli / BIOS reads are single-flight and reused for 20 s (stacked ACPI calls froze the PC).
- No network, by rule: tools are run by full System32 path (Services/SysExe.cs).

## 1.3 (2026-10-17)
- One pre-release for everything (Amine: one checklist, one test session). Risky parts sit behind gear > Debug toggles.
- CLI hash allowlist instead of "trust current files": settings.json is user-writable, so a stored override would
  defeat the check. New alienfx-tools versions need a new app build with their hashes.
- HardwareController is a queue for ACPI-level calls only (CLIs, AWCC WMI, BIOS WMI); the USB HID has its own lock.
- Ultimate detection: BootId over TickCount64 (Fast Startup keeps the tick count running).
- Keyboard timeout uses the existing G-key hook and mouse raw input registered only while dark: no new always-on hook.
- GPU % comes from Windows' GPU Engine counters for the adapter with the most dedicated memory in use (the RTX),
  never NVML, so it can't wake the RTX on battery.
- WPF-UI bumped to 4.3.0; the app only uses its theme dictionaries and SymbolIcon. Fallback: latest 3.x.
- beta.1 A/B on the laptop: the refresh-timed slide thread won over the 1.2 UI-thread slide, and the transparent
  layered window won over a normal DWM window (rounded corners + native shadow felt worse). Both toggles removed.

## 1.3.0-beta.2 (2026-10-17)
- Settings as a page inside the main window, not a tabbed popup: five tabs don't fit in the 290 px popup, and the
  popup was its own layered window (the beta.1 first-frame hitch). The page reuses the F3/F4 card swap. All tab panels
  stay in the layout (Hidden), so the page height is the tallest tab and tabs never resize the window.
- Run on startup stays in the footer AND in General (one setting): it's the one switch people look for on the main view.
- Soft + Pill are the defaults for upgrades too (Amine is the only beta user and picked them). Classic keeps the 1.2 look and sound.
- Only the active chime pack is cached; switching stops playback first, then frees the unmanaged buffers.
- 1.3.0: Amine shipped beta.2 straight to stable (Soft chimes crackled, the rest fine). Sounds moved from PlaySound to
  a one-buffer waveOut per sound: no app-side refill can starve, and the device is closed right after (zero idle).
  Hardware ACPI calls can still stall the whole machine; the 150 ms gap keeps the attack clean.
  Tested 2026-10-18: fixed as far as possible for now, much less noticeable. Not chasing it further in 1.3; if it
  matters later, try a longer gap or a shorter Soft tail first (ROADMAP > Later). The beta.2 checklist was skipped
  because Amine tested beta.2 directly and went to stable.
- The pill fades (instead of sliding) with Reduce motion / Windows animations off, as the directive asks; the
  classic card stays instant there, as in 1.2.

## 1.3.1-beta.2 (2026-10-18)
- Amine tested beta.1: the Off menu felt like a mess (lit = off was backwards, one click could flip twice, the popup
  covered the panel) and status keys went dark after Keyboard off / on. He asked for a redesign; mockups + plan were
  approved before any code (docs/mockups/1.3.1-beta.2).
- Tiles show the light state (lit = on); settings still store the "off" switches, so beta.1 settings carry over as is.
- All tile is an action, not a state: All off while anything is on, All on when everything is off (his rule).
  beta.3 (Amine): labels Turn off / Turn on, accent while lights are on, plain when all are off; no focus border.
- Double flip: a 350 ms debounce per menu, not per tile, so a double-click on any tile is one action.
- Status keys: SetOff(false) sends brightness 255 then the held frame; the firmware seems to miss a frame right after
  the brightness command (inferred: the swatch click, which sends the same frame later, always fixed it). So the frame
  is sent again 150 ms later. Static only.
- F7: no consumer report (crash.log). The F7 test is opt-in and 20 s only (it logs key presses, so never always-on).
  Amine keeps the F7 cap: F7's levels scale the app brightness (at 50%: off, 25%, 50%), F7 never goes above it.
- Toasts: Pill shortened rather than auto-sized, so every pill is the same width (calmer when they replace each other).

## 1.3.1-beta.1 (2026-10-18)
- Built directly by Hark at Amine's request (no separate plan round: the 1.3.1 directive and both mockups were the
  approved plan). Mockups: docs/mockups/1.3.1.
- Keyboard off = hardware brightness 0 + held frames (KeyboardHid.SetOff), not black. Why: 1.3.0 painted black and F7
  (firmware) then scaled black. The timeout already proved brightness 0 + held frames on this keyboard. F7's own report
  is not probed yet, so the app can't follow it: after F7 the tile still says Off, and new colors wait for the tile
  (checklist step 13). The probe log is there to fix that in 1.3.2 without guessing.
- Chassis zones stay black-painted (setone 0,0,0): setdim hits the power button and never reaches 0 on the ring.
- Turning a light off never changes the saved color any more; picking a color doesn't turn lights back on.
- The menu is a Popup like the custom color one (the 1.3 settings page hitch came from a big layered popup; this one is
  small). Tile style + Icon.Ring (option C "ring from above") + Icon.Alien (the app's logo.png as a mask) are the same
  block in both themes, also added to dev/gen_themes.py.
- Status keys go dark with the keyboard (hardware brightness covers every key); they come back with it.

