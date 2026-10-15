# Roadmap

## Bugs being fixed now
- None open. v0.8.6 should fix the invisible animations (cause found in the v0.8.5 log); waiting on a laptop test.
  If they still look dead, next suspect is the `AllowsTransparency` layered window dropping frames.

## Next after the bugs
- GPU modes: two modes since v0.9.2 (Advanced Optimus / Ultimate), waiting on a laptop test.
- Ultimate: the BIOS exposes `HybridGraphics = Disabled/Enabled` through Dell BIOS WMI (no BIOS password on this laptop),
  so Ultimate = set `Disabled` + restart, Optimus = `Enabled` + restart. Wired together with the GPU row.
- **Preset key**: done in v0.9.10 as F2 (Macro A, media mode): Quiet → Balanced → Performance, F2 tinted green / cyan /
  red. Battery is out of the cycle (later: only on battery power). Toast postponed.
- Done in v0.9.11: auto preset on AC / battery, toasts + sound for F1 / F2 / plug / unplug (Battery joins the F2
  cycle on battery only).
- Maybe later: bind F3 / F4 (Macro B / C, codes 02-52 / 02-53).

## Waiting on hardware tests
- v0.8.7 tray toggle and themed tray menu.
- v0.8.8 native window slide (open/close actually animates).
- v0.8.9 profile CPU boost, minimize slide.
- v0.8.10 accent icon fix.
- v0.9.11 auto preset, toasts, sounds.
- v0.9.0 GPU row (iGPU only, NVIDIA display mode hand-off, Ultimate via BIOS + restart).
- v0.8.6 animations (Quick Settings slide, timings to tune by eye).
- v0.8.0 to v0.8.2 (themes, gear menu, profiles, power readout, chip, animations) not yet run on the laptop.
- "Check Ultimate support" report: gives the exact Dell BIOS attribute for MUX and the right RAPL counter instance.

## GPU switching (next big step)
1. Amine records NVIDIA's own "Laptop display mode" switch with Sysinternals Process Monitor (filter: write ops only (RegSetValue, RegCreateKey, RegDeleteValue, RegDeleteKey, WriteFile, DeviceIoControl, Process Create, Load Image), all processes, System exclude unticked):
   - `1-gpu-only.csv`: Optimus to NVIDIA GPU only
   - `2-auto.csv`: NVIDIA GPU only to Automatic Select
   - `3-optimus.csv`: Automatic Select to Optimus
2. Read the registry / service / device operations around each switch and decide whether Windows-side replication is safe.
   Fallback: detect the mode and open NVIDIA's display-mode page for a one-click switch.
3. Wire the GPU buttons. iGPU only shows the themed "RTX in use" overlay first
   (mockups: `screenshots/gpu-warning-legend.jpg`, `screenshots/gpu-warning-fluent.jpg`).
4. Ultimate via Dell BIOS WMI once the attribute is confirmed; restart prompt; never from a profile.

## Later
- Long-term: Faster charging (Dell ExpressCharge via `PrimaryBattChargeCfg`), removed from the UI in v0.8.5.
- Direct WMI sensor reads instead of spawning alienfan-cli in the sensor loop (biggest CPU win).
- Done in v0.9.13: mute/mic/touchpad from callbacks instead of polling, memory trim in the tray.
- Keyboard backlight timeout when idle; effect speed; map keyboard columns for the rainbow wave.
- Trimmed, ReadyToRun publish. Target: under 0.3% CPU idle and 60 MB RAM.

## Not planned
- Overlay / FPS counter, driver updater, XMP toggle (stock RAM has no XMP profile).
