# Roadmap

## Road to 1.0
- [x] v0.9.13 idle trim: event-driven status keys, memory trim in the tray (~20 MB open, a few MB in the tray, 0% CPU).
- [x] v0.9.14 single-file ReadyToRun exe (no trimming, no compression).
- [x] v0.9.15 polish: Reset settings, About line, plainer error messages, docs cleanup.
- [x] v0.9.16 F3 opens / hides the app.
- [x] 1.0.0-rc1: full regression pass on the m15 R7 (every feature, both themes, AC / battery, Optimus / Ultimate).
- [x] 1.0.0: changelog summary, tag `v1.0.0`, GitHub Release zip. Real screenshots in the README.

## After 1.0
- Direct WMI sensor reads instead of spawning alienfan-cli in the sensor loop (biggest CPU win while the window is open).
- Keyboard backlight timeout when idle; effect speed; map keyboard columns for the rainbow wave.
- Fluent theme: native Windows 11 transparency (Acrylic blur of whatever is behind the window, like the Windows 11 Quick Settings flyout) instead of the flat gray background. Accent keeps following the laptop lights. Legend unchanged.
- Maybe: bind F4..F6 (Macro C..E, codes 02-53 .. 02-55).
- Maybe: toasts over fullscreen-exclusive games. Idea: detect it (`SHQueryUserNotificationState`) and fall back to the sound plus a short keyboard flash in the preset color. No in-game overlay or DLL injection.
- Maybe: faster charging (Dell ExpressCharge via `PrimaryBattChargeCfg`), removed from the UI in v0.8.5.

## Not planned
- Overlay / FPS counter, driver updater, XMP toggle (stock RAM has no XMP profile).
- NVIDIA private-call reverse engineering (GPU OC stays on public NVML offsets), forking or editing alienfx-tools.
- Profiles that change the GPU mode or use Ultimate.
