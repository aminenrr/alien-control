# Changelog

Each version was built by GitHub Actions (artifact `AlienControl-win-x64`). "Untested" means not yet run on the laptop.

## v0.8.10 (2026-10-15), untested
- Accent fix: the line icons (Legend section icons) kept the old accent until a second click on the swatch. After an
  accent change the window now re-reads every icon's color binding and repaints it once (one-shot walk, no timer).
- Legend: performance mode labels at 10.5 px too, so "Performance" fits in both themes.

## v0.8.9 (2026-10-15), untested
- Tray profiles now set CPU boost (AC and battery): Battery Off, Quiet Efficient, Balanced On, Performance Aggressive.
  The CPU boost row shows the level the profile set. Custom still changes nothing.
- Minimize slides down into the screen edge (170 ms, like hide), then minimizes; the taskbar button stays. Restoring
  (taskbar, tray, Alt+Tab) slides it back up.
- Fluent: performance mode labels at 10.5 px so "Performance" is no longer cut off.

## v0.8.8 (2026-10-15), tested OK (slide works; hover fades verified on video)
- Window slide fix, from Amine's 30 fps screen recording of v0.8.7: open and close had zero in-between frames (pop in,
  pop out). The storyboards ran, but WPF never presented the frames of content moving inside the transparent (layered)
  window.
  - The whole window now moves natively (`Services/WindowSlide.cs`, `SetWindowPos` once per frame, time based,
    cubic-bezier curves). DWM moves the already-drawn window, so no WPF repaint is needed for the motion.
  - Open: parked below the screen, drawn there, then rises out of the bottom edge in 250 ms (decelerate).
    Close: drops back past the edge in 170 ms (accelerate), then hides. Re-click mid-slide reverses from where it is.
  - Gear flyout: its popup window slides 14 DIP natively (160 ms in, 120 ms out); the fade is gone.
  - `ClipHost` and the card transform code removed. Per-frame hook only during a slide; idle cost unchanged.

## v0.8.7 (2026-10-15), untested
- Tray left click toggles: opens the window, or slides it back to the tray when it's already open (minimized restores).
- Tray right-click menu drawn in the app's look (`Services/TrayMenu.cs`, custom `ToolStripRenderer`): text in the accent
  color in both themes. Legend: `#0E1013`, mono font, caps, square corners, accent bar on hover. Fluent: Windows 11 dark
  gray, Segoe UI, rounded corners (DWM), soft rounded hover. Accent check on the current profile and a dot in each
  profile's tray tint. Restyles live on theme or accent change.

## v0.8.6 (2026-10-15), untested
- Animation fix, from the v0.8.5 motion.log: every storyboard ran and completed, but they weren't visible.
  - Window open/close now slides like the Windows 11 Quick Settings flyout: up out of the bottom edge (clipped by a new
    `ClipHost`) in 250 ms with the Fluent decelerate curve, back down in 170 ms with an accelerate curve. No fade.
  - The slide starts on the first rendered frame, and the hardware reads on show (state, GPU hint, refresh rate) wait until
    it has played. Before, a 160 ms open took 1004 ms of clock time behind that work, so it finished before the first frame.
  - Hover overlays now visible: new `HoverFxBrush` (Legend ~10% white, Fluent ~7%); Legend's old `#14191E` on `#0E1013`
    was about 3 RGB steps. Hover 150 ms, press 0.95 / 90 ms, release 160 ms, selection pulse 1.04 / 220 ms, underline 220 ms,
    profile chip 0.2 to 1 over 250 ms, gear flyout slides 14 DIP in 160 ms.
  - Link buttons (Check Ultimate support) and the RAM card got hover overlays (the RAM card is now a Grid).
- Temporary `Services/MotionLog.cs` and all its calls removed; no more `motion.log`.

## v0.8.5 (2026-10-15), untested
- Presets: only Battery uses 60 Hz; Quiet, Balanced and Performance use 240 Hz.
- Power readout every 1 s with a 2-sample average (was 2 s x 3), on its own timer, only while the window is open.
- Total on AC: estimate `~N W` = CPU + GPU + rest of the laptop. The rest is learned on battery (discharge minus CPU),
  starts at 15 W, saved as `RestOfSystemW` in settings. On battery the Total is still the real measured discharge.
- Faster charging (ExpressCharge) toggle removed; now a long-term roadmap item.
- GPU report: BIOS attribute current values were blank (WMI returns a string, not string[]); fixed. Also shows whether a BIOS password is set.
- TEMPORARY: `%AppData%\AlienControl\motion.log` (50 lines max per launch) to find why animations don't play. Remove with the fix.

## v0.8.4 (2026-10-15), untested
- Legend: section numbers 01-06 removed; the icon alone heads each section, like Fluent.
- Fluent: brightness % shown once (beside the slider); the duplicate in the Lighting header is gone.

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
