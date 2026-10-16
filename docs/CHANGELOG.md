# Changelog

Each version was built by GitHub Actions (artifact `AlienControl-win-x64`). "Untested" means not yet run on the laptop.
From 1.0.0 on, tagged versions are also on the GitHub Releases page as a zip.

## v1.1.0-beta.5 (2026-10-16), untested
- The header preset chip is gone. A header button (left of minimize) shows the active preset's icon in its color;
  clicking it opens a list of Battery, Quiet, Balanced, Performance and Custom, each with icon + color, the active one
  marked. Both themes. Battery is greyed on AC with Auto preset, like the mode button.

## v1.1.0-beta.4 (2026-10-16), untested
- Gear toggle "Show in taskbar" (on by default). Off: no taskbar button, the app only lives in the tray, and the
  minimize button slides it back to the tray.

## v1.1.0-beta.3 (2026-10-16), confirmed
- Compact view: the tray and F3 always open a short window (sensors, power, performance mode, lighting colors + Off).
  F4 opens the full menu. A long footer button (where the version was) flips Full menu / Compact both ways; the
  version moved to the right of the status line. The window stays anchored to its corner when it grows or shrinks.
- F3 / F4 logic: the key for the view already shown hides it, the other key switches the open window to its view.
  Same gear toggle as before ("F3 / F4 open the app").
- Fix (re-tagged): the footer uses three columns, so the view button no longer touches "Run on startup" in Fluent.

## v1.1.0-beta.2 (2026-10-16), withdrawn
- Tried a native Windows 11 Acrylic backdrop for Fluent. It didn't look good enough and added some CPU and RAM, so it was
  reverted and the pre-release removed. Fluent stays solid.

## v1.1.0-beta.1 (2026-10-16), confirmed
- Sensors (CPU / GPU temp, both fans) are read straight from the Alienware WMI (`AWCCWmiMethodFunction.Thermal_Information`)
  instead of starting `alienfan-cli temp rpm` every 2 s. Read-only, IDs found with `dev/wmi-probe.ps1`
  (fans 0x32 / 0x33, temps 0x01 / 0x06). Falls back to alienfan-cli automatically if WMI fails.
- Pre-release: v1.0.0 stays the Latest release.

## v1.0.0 (2026-10-15)
- First stable release, same code as rc1. Presets + F2, F1 G-Mode with GPU OC, F3 quick open, auto preset on AC /
  battery, toasts + sounds, fans / sensors / power, per-key lighting and status keys, Optimus / Ultimate switch,
  Legend + Fluent themes, Reset settings. Idle: 0% CPU, ~20 MB open, a few MB in the tray.
- README: real screenshots of both themes and the toasts (replacing the mockups).
- GitHub Release with `AlienControl-1.0.0-win-x64.zip` (built by `.github/workflows/release.yml` on the `v1.0.0` tag).

## v1.0.0-rc1 (2026-10-15), confirmed
- F2 no longer lights up in the preset color (too distracting); it stays the keyboard color.
- F1 / F2 toast and sound fire right on the key press instead of after the CLI calls (~1 s sooner). F2 into or out of
  Battery still waits for the refresh-rate change. No extra CPU or RAM.
- Release candidate: no new features. Version shows as `1.0.0-rc1` in the footer and the About line.
- Full hardware regression pass (checklist in docs/RC-CHECKLIST.md) before 1.0.0.

## v0.9.16 (2026-10-15), confirmed (smooth over borderless games; fullscreen exclusive switches focus to the app)
- **F3 opens the app** (Macro B, media mode, raw HID 02-52): opens the window, press again to hide it. If the window
  is open behind a game, F3 brings it to the front with focus. Gear toggle, on by default. No key color.
- Over borderless / windowed games the window shows on top; fullscreen-exclusive games will usually minimize (like Alt+Tab).

## v0.9.15 (2026-10-15), confirmed
- **Reset settings** (gear menu): confirm, then GPU OC back to +0, CPU boost restored, settings.json deleted and the
  app restarts with defaults. Run on startup, the BIOS and the GPU mode are left alone.
- About line in the gear menu: version and alienfx-tools credit.
- Plainer errors: a CLI failure says which part failed (Fans / power or Lighting) and what to try (timed out, access
  denied, device not found -> close AWCC). Missing CLI message names the missing exe; no tools folder needed.
- Docs: README feature list and setup, ROADMAP rewritten as the road to 1.0.

## v0.9.14 (2026-10-15), confirmed (RAM still ~20 MB open, feels faster)
- **One exe**: the artifact is a single `AlienControl.exe` (framework-dependent, still needs the .NET 8 Desktop
  Runtime) plus `tools/`. Precompiled with ReadyToRun so it starts and opens faster (less JIT on first use).
- No compression (it unpacks into RAM) and no trimming (WPF / WPF-UI use reflection). English-only resources.
- Settings stay in `%AppData%\AlienControl`; alienfan-cli / alienfx-cli stay as files in `tools/`.

## v0.9.13 (2026-10-15), confirmed (0% CPU, ~20 MB open, a few MB in the tray, was 98 MB)
- **Idle trim**. Status keys are event driven: speaker / mic mute from Windows audio callbacks (cached endpoints,
  re-bound when the default device changes), touchpad (F12) from a registry change notification. The 1 s poll that
  created two COM audio objects every second is gone; a 30 s safety net stays.
- Memory goes back to Windows ~3 s after the window hides, after a toast, and once after starting to the tray
  (compacting GC + working-set trim). Baseline before: 0.1% CPU, 98.1 MB (Task Manager).

## v0.9.12 (2026-10-15), confirmed
- Toast fix: it came out huge (rows stretched, window several times too tall at 125%). Now sized by hand to its content.
- Unplug toast was missing (sound played): after a refresh-rate change the toast and sound wait ~1.5 s for the screen
  to settle and stay up 2.2 s; if the display changes while a toast is up (rate change, Advanced Optimus moving the
  screen to the other GPU) it's shown again.

## v0.9.11 (2026-10-15), tested: toast too big, unplug toast missing
- **Auto preset** (on by default): unplug → Battery (leaves G-Mode first, OC back to +0) and remembers the AC preset;
  plug in → that preset again (Quiet / Balanced / Performance; after G-Mode or Custom it's Balanced, never G-Mode).
  Only if you're still on Battery; nothing changes at startup. Battery is greyed on AC (window + tray) while it's on.
- On battery F2 cycles Quiet → Balanced → Performance → Battery → Quiet; on AC Battery stays out.
- **Notifications**: small toast top-right (16 px in, ~1.6 s, no focus, click-through) with the mode icon, for F1
  (G-Mode on: fans max + OC; off: back to the preset), F2 (next preset + cycle pills) and plug / unplug. Window and
  tray clicks stay silent. Created per show, nothing running when idle.
- **Notification sound**: soft ticks (F2 tick, G-Mode up / down, unplug two notes down, plug in two notes up),
  embedded WAVs. Own toggle, works with toasts off.
- Gear menu: Auto preset, Notifications, Notification sound toggles (all on).

## v0.9.10 (2026-10-15), confirmed
- **F2 cycles presets**: F2 in media mode (Macro A, no Fn) goes Quiet → Balanced → Performance → Quiet; from
  Battery or Custom it starts at Quiet; in G-Mode it does nothing (F1 owns G-Mode). Read as raw HID from the keyboard
  (0D62:CABC report 02-51), so no AWCC, no admin, no polling, and Fn+F2 (plain F2) is untouched.
- F2's light shows the preset: Quiet green, Balanced cyan, Performance red; normal key color in G-Mode, Battery and
  Custom. Written in the existing one-frame keyboard update, only when the preset changes. Works with the static color
  and the software waves; firmware effects (breathing, pulse...) keep their own colors.
- Gear menu: **F2 cycles presets** toggle (on by default). F2 light id is a guess (2), verify on the laptop.

## v0.9.9 (2026-10-15), untested
- G-Mode is **white**: the G-Mode button (both themes), the header chip and the tray icon. Custom is now violet (#B266FF).
- G-Mode sets CPU boost to **Aggressive** (no cap) whatever it was, and puts the preset's level back when it ends
  (Custom: the level shown before, battery Efficient), also on Quit. Picking a boost by hand inside G-Mode wins.
  Replaces v0.9.8's cap-only lift.

## v0.9.8 (2026-10-15), untested
- Header chip + tray icon show **G-Mode** (violet) while it's on, instead of CUSTOM. Display only: the saved preset
  isn't touched, isn't in the tray menu, and comes back when G-Mode ends.
- G-Mode lifts Quiet's 3.0 GHz Efficient cap while it's on (boost mode itself unchanged) and puts it back after,
  also on Quit. Picking a CPU boost by hand inside G-Mode wins.
- Fluent is shorter: tighter cards, headers and buttons (Legend unchanged). Fluent button labels now really use
  their own font size (an empty TextBlock style in the templates blocks WPF-UI's 14 px default), which also fixes
  "Performance" being cut off.

## v0.9.7 (2026-10-15), tested: +200 / +200 gives 1665 / 7201 MHz in Control, back to stock outside G-Mode
- New **GPU Overclock** section (03, under GPU Mode): Core and Memory sliders side by side, +0 to +300 MHz in 25 MHz
  steps, default +200 / +200. AWCC-style values; memory is sent to NVML x2 (calibrated in Control).
- Works only in **G-Mode on AC**; greyed out otherwise. Leaving G-Mode, unplugging or quitting puts +0 back;
  entering G-Mode on AC reapplies the kept offsets; after sleep they are written again.
- Apply starts a 20 s keep-or-revert (the button row turns into Revert · countdown · Keep). No answer = +0.
  A test that never finished (crash, driver reset) is reset to +0 at the next launch.
- No stress test, voltage, power limit or fan controls. Services/GpuOc.cs (nvmlDeviceGet/SetClockOffsets, P0,
  driver range checked before every write, NVML opened only per call). Sections 03 to 06 renumbered 04 to 07.

## v0.9.6 (2026-10-15), untested
- Removed the disabled "Custom curve…" fan button. Fans row is Auto / Max Boost.

## v0.9.5 (2026-10-15), untested
- Efficient boost cap lowered to 3.0 GHz (Quiet is day to day: Discord, Vivaldi, Steam).
- GPU hint uses full names: "RTX 3070 Ti" and "Intel Iris Xe" (e.g. OPTIMUS · Intel Iris Xe, Ultimate = RTX 3070 Ti).

## v0.9.4 (2026-10-15), untested
- Efficient CPU boost (Quiet) is capped at 3.4 GHz through Windows "Maximum processor frequency" (PROCFREQMAX and
  PROCFREQMAX1, active plan, AC + battery). Any other boost level removes the cap. Services/CpuFreq.cs.

## v0.9.3 (2026-10-15), untested
- Fix: after switching to Ultimate and restarting, the row still showed Advanced Optimus + "Ultimate after restart".
  On the m15 R7 the Intel GPU stays listed in Ultimate, so detection now trusts the BIOS value, unless the app changed
  it after the last boot (`GpuSwitchSetUtc` in settings).

## v0.9.2 (2026-10-15), untested
- GPU row cut to two modes: **Advanced Optimus** and **Ultimate**. iGPU only, dGPU only, the RTX-off switch,
  the "RTX in use" dialog, the NVIDIA Control Panel hand-off and "Check Ultimate support" are gone.
- Hint (accent colored): Advanced Optimus shows NVIDIA's mode + the GPU on the screen (OPTIMUS · IRIS Xe,
  AUTO · IRIS Xe / 3070 Ti, dGPU ONLY · 3070 Ti); Ultimate shows just 3070 Ti. Re-read on show, display change and focus.
- Switching either way = Dell BIOS HybridGraphics, themed dialog Cancel / Restart later / Restart now.
  Clicking the running mode does nothing (or cancels a switch queued for the next restart).

## v0.9.1 (2026-10-15), untested
- GPU hint names the GPU actually driving the screen: Optimus / Auto on Intel show "Iris Xe", not "3070 Ti".

## v0.9.0 (2026-10-15), untested
- GPU row is live. It shows the real mode (which GPU drives the panel, live; NVIDIA's Auto flag from the registry),
  read on show, after a change and on display changes. No polling.
- iGPU only: in-window "RTX in use" dialog (Cancel / Refresh / Switch anyway) listing apps holding RTX memory, then
  pnputil disables the RTX. Blocked while the RTX drives the screen (switch to Optimus first).
- Optimus / dGPU only: NVIDIA has no API, so the app opens the NVIDIA Control Panel (Manage display mode) and the row
  follows once the screen switches. From iGPU only, Optimus just re-enables the RTX.
- Ultimate: Dell BIOS HybridGraphics, dialog with Cancel / Restart later / Restart now. In Ultimate, iGPU only and
  dGPU only are disabled; Optimus turns hybrid back on (restart). Hint shows "Ultimate/Optimus after restart" while pending;
  picking a hybrid mode while Ultimate is queued just undoes it. The RTX is re-enabled first if it was off.
- GPU report now includes NVIDIA's InternalMux flags.

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
