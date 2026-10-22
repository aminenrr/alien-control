# Roadmap

## Road to 1.0
- [x] v0.9.13 idle trim: event-driven status keys, memory trim in the tray (~20 MB open, a few MB in the tray, 0% CPU).
- [x] v0.9.14 single-file ReadyToRun exe (no trimming, no compression).
- [x] v0.9.15 polish: Reset settings, About line, plainer error messages, docs cleanup.
- [x] v0.9.16 F3 opens / hides the app.
- [x] 1.0.0-rc1: full regression pass on the m15 R7 (every feature, both themes, AC / battery, Optimus / Ultimate).
- [x] 1.0.0: changelog summary, tag `v1.0.0`, GitHub Release zip. Real screenshots in the README.

## 1.1
- [x] 1.1.0-beta.1 Direct WMI sensors: temps and fans read straight from WMI (probe first), alienfan-cli kept for commands and as fallback.
- [-] 1.1.0-beta.2 Fluent Acrylic: tried and dropped (see Not planned).
- [x] 1.1.0-beta.3 Compact view: always opens compact (header, sensors, power, performance mode, lighting row). A long footer button flips Full menu / Compact, and the version label moves above it. F3 = compact, F4 = full (macro index 2, 02-53).
- [x] 1.1.0-beta.4 Show in taskbar toggle (off = tray only).
- [x] 1.1.0-beta.5 Header preset button + preset list (replaces the chip).
- [x] 1.1.0 released 2026-10-16 (each beta was confirmed on the laptop, so no separate rc).
- [x] 1.2.0 released (2026-10-17).
- [x] 1.2.0-beta.1 Toasts + chimes v2 (Dolby Controls style).
- [x] 1.2.0-beta.2 iGPU only (RTX off) as a third GPU mode, NVIDIA Optimus only.
- [x] 1.2.0-beta.3 Smooth compact/full switch, snappier open/close, chime crackle fix.
- [x] 1.2.0-beta.4 iGPU only crash fix, lock badges, note on click.
- [x] 1.2.0-beta.5 Card-swap view switch, no freeze when spamming F3/F4.

## 1.3
- [x] 1.3.0-beta.1 (one pre-release, tested with docs/BETA-1.3.0-CHECKLIST.md): code review fixes #1-#19, keyboard
  light timeout, CPU % / GPU % row, refresh-timed slide engine (normal-window option tried and dropped).
- [x] 1.3.0-beta.2: chime pack (Soft / Classic), toast style (Pill / Classic), settings page with tabs.
- [x] 1.3.0 (2026-10-18): stable, tested on the m15 R7, plus the chime crackle fix (best effort for now, much less
  noticeable).
- [x] 1.3.1-beta.1: Off menu, F7 after Off, Spectrum / Rainbow restart fix. Tested: menu redesigned in beta.2.
- [ ] 1.3.1-beta.4: beta.3 + F7 test on every Alienware HID collection.
- [ ] 1.3.1 stable (Amine): remove Settings > Advanced > "Log frame gaps" and "F7 test" (debug only, not needed).
  Also (Amine): update CHANGELOG / release notes with what's new and the tests done (beta.1-4 results), and the README's
  features section with the Lights menu, rounder / shorter notifications, using the 1.3.1 mockups as images.
- [-] 1.3.1-beta.3 (superseded by beta.4): Keyboard off really off (brightness 0 after every CLI run), Turn off / Turn on tile, accent while on, no focus border.
- [x] 1.3.1-beta.2: Lights menu (laptop button, lit = on, All off / All on), status-key fix, F7 test, Classic toast
  corners, shorter Pill. Test with docs/BETA-1.3.1-CHECKLIST.md.
- [ ] 1.3.1-beta.3 (only if the F7 test shows something): the Keyboard tile follows F7.
- [x] 1.1.1 patch: shows as "Alien Controls" in startup lists, startup task renamed (old one migrated).

## 1.3.4
- [x] 1.3.4-beta.1..4 (pre-releases, 2026-10-21): GPU OC in the Performance preset (header toggle), accent hint,
  OC state on toasts (Classic: own line). PR #5.
- [x] 1.3.4 (2026-10-21): tested on the m15 R7, PR #5 merged. Built from main, so it missed 1.3.1 to 1.3.3.
- [x] 1.3.5 (2026-10-22): 1.3.3 + 1.3.4 merged (Lights menu back), OC tag on its own pill line, Lights toast in the
  accent colour. Tested (beta.1), main brought up to v1.3.

## Later
- Effect speed; map keyboard columns for the rainbow wave.
- Maybe: remove the last faint chime crackle during hardware switches (longer gap before the ACPI calls, or a
  shorter Soft tail).
- Maybe: bind F5..F6 (Macro D..E, codes 02-54 .. 02-55).
- Maybe: toasts over fullscreen-exclusive games. Idea: detect it (`SHQueryUserNotificationState`) and fall back to the sound plus a short keyboard flash in the preset color. No in-game overlay or DLL injection.
- Maybe: faster charging (Dell ExpressCharge via `PrimaryBattChargeCfg`), removed from the UI in v0.8.5.

## Not planned
- Fluent Acrylic / see-through blur: tried in 1.1.0-beta.2 (native Windows 11 backdrop). It didn't look good enough and cost extra CPU and RAM, so it was removed.
- Overlay / FPS counter, driver updater, XMP toggle (stock RAM has no XMP profile).
- NVIDIA private-call reverse engineering (GPU OC stays on public NVML offsets), forking or editing alienfx-tools.
- Profiles that change the GPU mode or use Ultimate.
