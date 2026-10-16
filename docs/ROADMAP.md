# Roadmap

## Road to 1.0
- [x] v0.9.13 idle trim: event-driven status keys, memory trim in the tray (~20 MB open, a few MB in the tray, 0% CPU).
- [x] v0.9.14 single-file ReadyToRun exe (no trimming, no compression).
- [x] v0.9.15 polish: Reset settings, About line, plainer error messages, docs cleanup.
- [x] v0.9.16 F3 opens / hides the app.
- [x] 1.0.0-rc1: full regression pass on the m15 R7 (every feature, both themes, AC / battery, Optimus / Ultimate).
- [x] 1.0.0: changelog summary, tag `v1.0.0`, GitHub Release zip. Real screenshots in the README.

## 1.1
- [ ] 1.1.0-beta.1 Direct WMI sensors: temps and fans read straight from WMI (probe first), alienfan-cli kept for commands and as fallback.
- [ ] 1.1.0-beta.2 Fluent Acrylic: Quick Settings style blur, accent still follows the laptop lights, solid fallback. Legend unchanged.
- [ ] 1.1.0-beta.3 Compact view: always opens compact (header, sensors, power, performance mode, lighting row). A long footer button flips Full menu / Compact, and the version label moves above it. F3 = compact, F4 = full.
- [ ] 1.1.0-rc1 full regression, then 1.1.0 release.

## Later
- Keyboard backlight timeout when idle; effect speed; map keyboard columns for the rainbow wave.
- Maybe: bind F5..F6 (Macro D..E, codes 02-54 .. 02-55).
- Maybe: toasts over fullscreen-exclusive games. Idea: detect it (`SHQueryUserNotificationState`) and fall back to the sound plus a short keyboard flash in the preset color. No in-game overlay or DLL injection.
- Maybe: faster charging (Dell ExpressCharge via `PrimaryBattChargeCfg`), removed from the UI in v0.8.5.

## Not planned
- Overlay / FPS counter, driver updater, XMP toggle (stock RAM has no XMP profile).
- NVIDIA private-call reverse engineering (GPU OC stays on public NVML offsets), forking or editing alienfx-tools.
- Profiles that change the GPU mode or use Ultimate.
