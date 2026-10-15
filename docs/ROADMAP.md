# Roadmap

## Waiting on hardware tests
- v0.8.0 to v0.8.2 (themes, gear menu, profiles, power readout, chip, animations) not yet run on the laptop.
- "Check Ultimate support" report: gives the exact Dell BIOS attribute for MUX and the right RAPL counter instance.

## GPU switching (next big step)
1. Amine records NVIDIA's own "Laptop display mode" switch with Sysinternals Process Monitor (filter: process name contains `nv`):
   - `1-gpu-only.csv`: Optimus to NVIDIA GPU only
   - `2-auto.csv`: NVIDIA GPU only to Automatic Select
   - `3-optimus.csv`: Automatic Select to Optimus
2. Read the registry / service / device operations around each switch and decide whether Windows-side replication is safe.
   Fallback: detect the mode and open NVIDIA's display-mode page for a one-click switch.
3. Wire the GPU buttons. iGPU only shows the themed "RTX in use" overlay first
   (mockups: `screenshots/gpu-warning-legend.jpg`, `screenshots/gpu-warning-fluent.jpg`).
4. Ultimate via Dell BIOS WMI once the attribute is confirmed; restart prompt; never from a profile.

## Later
- Custom fan curve (alienfan `unlock` + boost per temperature; loop only while a curve is active).
- Direct WMI sensor reads instead of spawning alienfan-cli in the sensor loop (biggest CPU win).
- Mute/mic state from audio endpoint callbacks instead of polling.
- Keyboard backlight timeout when idle; effect speed; map keyboard columns for the rainbow wave.
- Trimmed, ReadyToRun publish. Target: under 0.3% CPU idle and 60 MB RAM.

## Not planned
- Overlay / FPS counter, driver updater, XMP toggle (stock RAM has no XMP profile).
