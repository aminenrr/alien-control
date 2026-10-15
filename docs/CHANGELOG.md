# Changelog

Each version was built by GitHub Actions (artifact `AlienControl-win-x64`). "Untested" means not yet run on the laptop.

## v0.8.3 (2026-10-15), untested
- Sharper text and icons: the window shadow moved to its own layer behind the card. Before, the shadow effect sat on the
  card itself, so WPF drew every text and icon through an offscreen bitmap (soft, no ClearType, blurry at 125%).
- Layout rounding on, ClearType enabled on the opaque card and popups, and animations remove their transforms when they finish.
- Fluent background is now a neutral Windows 11 dark gray; the old Mica-style gradient ended in a red-purple tint at the bottom.

## v0.8.2 (2026-10-15), untested
- Subtle animations: window open/close fade + slide, button hover/press, selection pop, profile-chip fade, gear flyout.
  All in `Motion.cs`; Opacity/RenderTransform only, one-shot, 60 fps, nothing runs while hidden.
- "Reduce motion" toggle in the gear menu (default off) makes the app fully static. Settings v6.
- Gear flyout now closes on outside click, deactivate, minimize and hide.

## v0.8.1 (2026-10-15), untested
- Read-only current-profile chip next to "m15 R7", tinted like the tray icon
  (Battery #FFB020, Quiet #3CFF7A, Balanced #00E5FF, Performance #FF4D6A, Custom white).

## v0.8.0 (2026-10-15), untested
- Redesign with **Legend** and **Fluent** themes, switchable in a new gear menu (persisted).
- Battery power mode, Refresh Rate row (60 / 240 Hz), power readout (CPU / GPU / total on battery).
- Tray profiles Battery / Quiet / Balanced / Performance / Custom with tinted tray icon (power mode + refresh rate).
- Experimental Faster charging (Dell BIOS ExpressCharge, hidden if not exposed).
- G-key always on. GPU row renamed iGPU only / Optimus / dGPU only / Ultimate (preview only) with a
  "Check Ultimate support" report. Settings v5.

## v0.7.0 (2026-10-15)
- RAM card: click = quick clean, right-click = deep clean (`MemoryCleaner.cs`).
- First GPU mode code (`GpuMode.cs`): adapter disable/enable, Dell BIOS WMI MUX lookup, clipboard report.

## v0.6 to v0.6.3 (2026-10-15)
- Effect dropdown with every APIv5 effect; chassis mirrors breathing, pulse and morph.
- Custom color picker (hue, saturation, then R/G/B boxes).
- Spectrum and Rainbow wave as software animations, then made ~1.7x faster.

## v0.5 to v0.5.3 (2026-10-15)
- Renamed **Alien Controls**, Alienware logo, Performance = index 2, window snaps above the auto-hide taskbar.
- Fans in %, RAM + VRAM row, accent follows the lighting color.
- Keyboard written in one HID frame (status keys fixed); software brightness.

## v0.4 (2026-10-14)
- White Alienhead icon, borderless styled window, pinned bottom-right, topmost.

## v0.3 / v0.3.1 (2026-10-14)
- Status keys emulated (white when active), last color reapplied at start.
- Chassis map fixed: ring = IDs 0 + 1, power button (ID 2) never sent.

## v0.2 (2026-10-14)
- Verified power-mode map, G-key hook with Detect, per-key lighting, Breathe/Wave, GPU limit removed.

## v0.1 (2026-10-14)
- Skeleton: WPF + WPF-UI, tray, CLI runner, power/fans/lighting buttons.
