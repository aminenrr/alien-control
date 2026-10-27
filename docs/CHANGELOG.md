# Changelog

Each version was built by GitHub Actions (artifact `AlienControl-win-x64`). "Untested" means not yet run on the laptop.
From 1.0.0 on, tagged versions are also on the GitHub Releases page as a zip.

## v2.0.3 (2026-10-27)
- Overlay latency (ported from diag/overlay-latency, PR #25, stock PresentMon 2.6.0 only): our PresentMon ETW session is flushed every 50 ms (ControlTrace FLUSH, Services/EtwFlush.cs), so frames reach the overlay ~0.18 s old instead of ~1.6 s (measured on diag, no event loss, no CPU cost). The graph Playhead holds a fixed 180 ms behind PresentMon instead of the adaptive delay (Amine; 120 ms ran dry 8 to 14 % of the time). No patched exe, no diag logging.
- GPU USE reads like RTSS: with the RTX on and awake, the driver's graphics load (NvAPI_GPU_GetDynamicPstatesInfoEx, utilization[0]); the PDH engtype_3D sum read 10 to 15 points low. Optimus only asks the driver while PDH already sees the RTX busy, so it never wakes a sleeping RTX; iGPU only, a GPU switch or a failed call keep the PDH value.
- Latency rows match on dGPU and Optimus (PR #26): PresentMon's display latency is always kept and shown as DISP LAT in both modes, with PC LAT (Reflex) above it when the game sends it. Level 2 shows PC lat / Disp lat.
- Overlay tweaks (Amine): the level 2 sync row follows the GPU that drives the panel: VRR On / Off in Intel blue when the Intel GPU does (Optimus, read through IGCL ctlGetDisplayProperties, Services/IntelVrr.cs), G-SYNC On / Off in NVIDIA green on dGPU. Levels 3 and 4: rows grouped with a small gap between clusters (time, GPU, CPU + cores, VRAM + RAM, BATT, FANS, DISPLAY + BOUND + LAT + MODE, FPS + lows + graph), FPS moved under MODE, FAN renamed FANS, DISPLAY row carries the same VRR / G-SYNC tag after the Hz, (Intel side: no counted-refresh guess, it reads 240 under VRR; IGCL reply logged once per change as "IntelVrr:" in crash.log), one LAT row (display latency, plus PC latency when Reflex).

## v2.0.2 (2026-10-26)
- Stability fixes (PR #24, audit 2026-10-26, items picked by Amine):
- Resume in G-Mode: a failed (null) G-Mode read no longer turns G-Mode off or applies a conflicting preset; it stays unknown and retries once. Successful reads update the G-Mode state, and the resume OC sync checks the real driver offsets.
- alienfan-cli timeouts: a timed out process is killed and its exit confirmed before the next hardware command runs; the hardware queue stays held if it survives, and other commands fail fast meanwhile.
- Quit during an OC write: the write in flight is awaited (bounded) before the +0 reset; the recovery marker is kept if the reset is skipped or fails.
- GPU mode switching: a second iGPU / Optimus switch is refused while the first is still pending, also after a timeout.
- OC test recovery: Revert, timeout and a failed apply keep the recovery marker until +0 is confirmed; only Keep clears it. Startup recovery waits while the RTX is off and runs when it comes back.

## v2.0.1 (2026-10-26)
- Overlay Move works like the NVIDIA overlay: both steppers count from the chosen corner toward the screen centre (0 = in the corner, + = closer to the centre) on every corner. Top / bottom centre keep a left / right shift. Fixes left / right doing nothing on the right side spots (the overlay was clamped at the edge while the number kept climbing). Old values convert once (Settings.OverlayMoveV2).
- GPU OC pill: PERF first, then G-MODE, each lit in its mode's own colour (Performance red #FF4D6A, G-Mode white) instead of the accent.
- Level 2 (Metal): FPS / 1% low / Frame show "--" when no game is running instead of 0.00. G-SYNC On / Off comes from the NVIDIA driver (NvAPI_Disp_GetVRRInfo) when the RTX drives the panel, the old guess otherwise.

## v2.0.0 (2026-10-25, stable)
- 2.0 stable: beta3.6 tested on hardware, no code changes. `main` now points at 2.0.

## v2.0.0-beta3.6 (2026-10-25, prerelease)
- Level 2 font: bundled JetBrains Mono NL SemiBold / Bold (Overlay/MonoFont.cs, embedded, extracted to %LocalAppData%\AlienControl\Fonts, private DirectWrite font set / WPF folder font). Cascadia Mono / Consolas fallback.

## v2.0.0-beta3.5 (2026-10-25, prerelease)
- Level 2: FPS max / Frame min from the fastest 10 frame average (uneven pacing read 4651 fps), FPS capped at 9999.
- Level 2 graph: scale 4x the frame interval (min 20 ms), red only for real hitches (2.5x the interval and +10 ms), so a zigzag no longer paints the whole graph red.

## v2.0.0-beta3.4 (2026-10-25, prerelease)
- Metal grid 32 columns: Mem bracket keeps a space between VRAM and RAM.
- Folds in beta3.3 (never published, release notes lacked a test list):
- Graph min / max label over the 200 frames the graph draws (was 2 s).
- Overlay perf log: a refresh only counts as missed against the slower of panel Hz and game fps (G-SYNC), and logs the game fps.
- README / STATUS brought up to date for 2.0.

## v2.0.0-beta3.2 (2026-10-25, prerelease)
Metal level on a 30 column mono grid (MSeg / MRow, Br rows), brackets per mock metal3.png.

## v2.0.0-beta3.1 (2026-10-25, prerelease)
Metal level: IOverlayCanvas.FillRounded (D2D FillRoundedRectangle, WPF DrawRoundedRectangle), mono SemiBold, MFs 20, MPad 12.

## v2.0.0-beta3 (2026-10-25, prerelease)
Untested. Levels: FPS only dropped, old L2 strip -> L1, new L2 Metal HUD (OverlayView.BuildMetal / RenderMetal, mono face via IOverlayCanvas.Mono, Item.Resolution / Item.Gsync from the refresh poll, GsyncState from vblank vs mode Hz). Settings.OverlayLevelsV3 moves level + looks once.

## v2.0.0-beta2.5 (2026-10-25, prerelease)
Untested. FPS from MsBetweenPresents again with the latency toggle on (was display change: 117 to 120 vs RTSS 140 in Control FG); undisplayed frames kept as Hidden, skipped by graph, lows, stutter, bound.

## v2.0.0-beta2.4 (2026-10-25, prerelease)
Untested. Graph back to one step per frame (200 frames, MangoHud / RTSS style) with a subpixel slide (GraphFrac); fixes wide hitch triangles and straight lines over pauses.

## v2.0.0-beta2.3 (2026-10-25, prerelease)
Untested. Latency toggle on: frametime = MsBetweenDisplayChange (frames not displayed dropped), fixes the FG zigzag. LATENCY label renamed DISP LAT.

## v2.0.0-beta2.2 (2026-10-25, prerelease)
FPS and graph fixed again (Amine: beta2.1 still 0 FPS). Untested.
- Lean PresentMon args back to ov.14 (no --track_frame_type): frame type without display tracking overflowed PresentMon's present buffer.
- FPS, graph and lows count every present like RTSS; GuessGenerated and the real-frame-only math removed.

## v2.0.0-beta2.1 (2026-10-25, prerelease)
Fixes beta2's 0 FPS and dead frametime graph (Amine, Castlevania + Control). Untested.
- Only real frame gen types count as generated (PresentMon 2.6 sends Unspecified, which beta2 counted as generated, leaving no real frames).
- CPUStartTimeInMs / TimeInMs time columns read (ms to seconds), arrival time fallback.
- PresentMon --set_circular_buffer_size 8192 (dropped presents in the log).

## v2.0.0-beta2 (2026-10-25, prerelease)
The in-game overlay (beta1.3-ov) merged into 2.0. Full notes to follow.

## v2.0.0-beta1.6 (2026-10-24, prerelease)
Updating takes over from the running copy (Amine). Tested on the laptop 2026-10-24 (the hand over itself needs the next version to test).
- **Newer copy takes over** (Amine: beta1.5 started while beta1.4 sat in the tray only woke beta1.4, so the Program
  Files copy stayed on beta1.4). In `App.OnStartup`, a second copy whose version is newer than the running one
  (`RunningIsOlder`: the other process's exe ProductVersion vs this InformationalVersion, `Startup.CompareVersions`)
  sets `Local\AlienControls.Quit` and waits up to 15 s for the single-instance mutex. The running copy's `HandOver`
  does Quit's cleanup without the iGPU prompt, releases the mutex and exits. Older copies don't listen: the wait times
  out and the new copy wakes them and exits as before.
- `Startup.TryGetXml` treats `FileNotFoundException` (late-bound COM "task not found") as not found instead of
  logging a warning at every start.

## v2.0.0-beta1.5 (2026-10-24, prerelease)
Fix for beta1.4's startup copy, found by Amine on the laptop. Tested on the laptop 2026-10-24.
- **Startup copy missed the CLIs** (Amine): `Startup.AppFiles` only took the alienfx-tools CLIs from `tools\`. With
  `alienfan-cli.exe` / `alienfx-cli.exe` next to the exe (Amine's `C:\AlienFX`), the Program Files copy had none, so
  after a reboot `AlienFan.IsAvailable` was false: Home sensors showed `--` and presets, G-Mode and shortcuts did
  nothing. Root-level CLIs are copied now (CliRunner still hash-checks them). Test `AppFiles_takes_root_clis`.
- **NVIDIA tray check**: `EnsureNvidiaTrayAsync` waited for `nvcontainer` (the NVIDIA app's container, always running),
  so it reported "up" at once. It now waits for an `NVDisplay.Container` process in the session started after the
  restart. beta1.4's restart itself worked 3 out of 3 on the laptop after a reboot.

## v2.0.0-beta1.4 (2026-10-23, prerelease)
Settings save fix, Tier 2 (performance) and Tier 3 (cleanup) from the 2.0 code review, and six visual changes
(Amine's calls). Tested on the laptop 2026-10-24 (startup copy CLI bug fixed in beta1.5).
- Run on startup now runs a copy in Program Files, so other programs can't swap the file that starts as admin at logon.
  Turning it on copies the exe, `tools\` (alienfx-tools CLIs), LICENSE and README to `%ProgramFiles%\AlienControl`
  (protected ACL: SYSTEM and Administrators full, Users read; every file owned by Administrators) and points the task
  there. `Startup.IsAdminOnly` checks the folder, `tools\`, the exe and the parent folders; the task is only ever
  registered or repointed (`NeedsRebuild`) to a path that passes. On start, an older task pointing at a user-writable
  folder is moved to Program Files, or deleted with the toggle off and a status line if that fails; a newer version run
  from elsewhere updates the copy (staged `*.new`, swapped, a file in use renamed to `*.old`). The task is registered
  through the Task Scheduler COM API (late-bound `Schedule.Service`, XML as a string): the `%TEMP%` XML file and the
  beta1.3 pin / read-back code are gone. Turning it off deletes the task and removes the Program Files copy (Amine);
  when that copy is the one running, its files go at the next reboot (MoveFileEx) or on the next start from elsewhere.
  `%AppData%\AlienControl` is never touched.
- Removed the Log frame gaps debug option. (Settings > Advanced row, the `FrameLog` key, which is now dropped from old
  settings.json files on load, and `WindowSlide.LogFrames` with its per-slide crash.log lines; Reset settings is the
  first Advanced row and the freed height is spread over the tab, Amine.)
- **NVIDIA tray icon after iGPU only** (Amine): going back to Advanced Optimus left NVIDIA's display mode icon gone
  until a reboot (1.3.x brought it back). Regression from beta1.3: `net stop NVDisplay.ContainerLocalSystem` ran with a
  60 s kill, then `net start` right away, so a slow stop was killed and the start hit a service still stopping. It was
  also skipped whenever any nvcontainer process was in the session. Now the app waits for the RTX to report OK, always
  stops the service through the service manager, waits until it is stopped, starts it, waits until it runs, logs the
  outcome (`NvidiaTray` in crash.log), and does all of it on a pool thread outside the command gate. A pnputil that
  timed out but finished later still gets the restart.
- **GPU overclock while the RTX is off** (Amine): in iGPU only (or with no NVML) the Tuning OC card is greyed
  (sliders, Reset, Apply, G-MODE / + PERF), shows RTX IS OFF / OC in Advanced Optimus, and no OC write is attempted
  (startup +0, Reset, quit included). Home's G-Mode line reads "GPU overclock off while the RTX is off" and toasts show
  no OC tag. Everything comes back with the RTX, and the kept offsets are re-applied in G-Mode.
- **Settings save**: settings.json is read with shared access and closed at once. Saves clear read-only flags, retry
  File.Replace (about 200 ms), then copy over the file; if that fails too the newest settings are kept and written by
  the next save, a 2 s retry timer or on quit (`Settings.FlushPending`). One warning per failure streak in crash.log
  (Amine's log had ~28 lost saves in 6 minutes). A torn file is moved aside as `settings.json.corrupt`. Unknown keys are
  kept (`[JsonExtensionData]`), one shared `JsonSerializerOptions`.
- **Performance (Tier 2)**: one PDH GPU query (utilisation + VRAM) read off the UI thread, Home reads off the UI thread;
  presets use powrprof in-process instead of 5 powercfg.exe runs; one getgmode at startup shared by lighting and OC;
  the GPU WMI query (Win32_PnPEntity) only on Tuning, at most every 10 s; the NVML session closes on battery, hidden and
  minimized; the light animation reuses its buffers (no per-frame dictionary or per-key arrays); accent and profile
  changes return early when nothing changed.
- **Packages**: WPF-UI and System.Diagnostics.PerformanceCounter removed. ToolTip, TextBox and ScrollBar have Legend
  styles. Published exe 11.6 MB to 3.7 MB, zip about 5.0 MB to 2.3 MB (measured on a Linux cross-publish).
- **Cleanup (Tier 3)**: dead code removed (20 s F7 probe, `GpuMode.Report`, Fluent theme branches, compact-mode
  leftovers, DellBios password / Express charge and related unused calls, F2 key overrides); 69 dead theme keys pruned;
  MainWindow.xaml.cs split into partials (Gpu, Modes, Sensors, Window added to the existing ones); hint text contrast
  #76828E (4.86:1); icon-only buttons have AutomationProperties.Name; an exception in a UI handler is logged and shown
  in the status line instead of closing the app; HID re-search backs off 1.5 s after a failed search; slider keys
  commit.
- **Build**: Dependabot config, CI NuGet cache and concurrency (newer push cancels the old run), `dev/check_xaml_keys.py`
  replaces the PowerShell key check; release.yml checks the tag against InformationalVersion and the release notes
  before building, and ships LICENSE in the zip. New tests (ThemeAndReleaseTests.cs): theme keys load and resolve,
  no dashes in UI text, swatches match the colour they send, WAVs load, version has notes, settings round trip, tints.
  The theme generator is retired to `dev/retired/gen_themes.py`; Legend.xaml is hand-edited.
- **Visual (Amine)**: green swatch #3CFF7A (matches the keys); selected fills are a dark tint of the lighting colour
  (`AccentSelBg` / `AccentPillBg`; zone tiles use their own colour; white gives a weaker grey; Rainbow and Spectrum use
  cyan fills); keyboard-only focus ring; the window scales down when the work area is shorter than 848 DIP; Legend
  tooltips (UI font, normal case, wrap at 320).
- **GPU tiles show which GPU they read** (Amine, mockup `docs/mockups/2.0-beta1.4/6-gpu-source.png`): a small accent
  chip (`GpuSourceChip2`: mono semibold, 1 px `AccentBrush` border on `AccentPillBg`, 3 px corners) next to the Home
  GPU and GPU USE labels and the Tuning strip's GPU reads INTEL in iGPU only and NVIDIA in Advanced Optimus / Ultimate,
  with a tooltip and AutomationProperties.Name, hidden while the mode is unknown or the RTX is switching. In iGPU only
  GPU USE now shows the Intel GPU's 3D engine use (was "Off"): `SystemStats.IntelGpuUsage()` sums the same PDH
  `GPU Engine(*engtype_3D)` query, filtered to the Intel adapter's LUID (DXGI `EnumAdapters`, VendorId 0x8086, looked
  up only in iGPU only); the RTX sum keeps its own LUID and never adopts the Intel one. The GPU temp in iGPU only is the
  Intel GPU through Intel IGCL (`Services/IntelGpuTemp.cs`: ControlLib.dll from System32, ctlInit with Level Zero,
  GPU-type temperature sensor, the device max as a second choice) instead of the AWCC board sensor by the RTX; any
  IGCL failure or a missing sensor disables it for the session and the tile shows the CPU package temperature (same
  die). crash.log gets one `iGPU temp: IGCL` or `iGPU temp: CPU package (...)` line. Bars and colours unchanged. New
  tests: IGCL struct sizes (36 / 24 bytes), PDH LUID name format.

## v2.0.0-beta1.3 (2026-10-23, prerelease)
Tier 1 fixes from the 2.0 code review (services + UI). Tested on the laptop 2026-10-23.
- **NVML**: one shared session (`NvmlSession`, one lock, one handle) for the GPU overclock and the power readout.
  RTX off / on calls `BeginDeviceRemoval` / `EndDeviceRemoval`, so no NVML call runs while pnputil removes or re-adds
  the RTX (an in-flight OC write could, the 1.2.0-beta.4 crash class). `PowerMeter.Dispose` takes the lock. The OC
  range check and the both-or-zero rollback are unchanged.
- **Crash, logoff, shutdown**: the two fatal crash handlers and Windows ending the session put the OC back to +0 and
  clear the CPU cap (synchronous, bounded). Startup reads the driver's offsets on AC (`GpuOc.Read`, the same
  `nvmlDeviceGetClockOffsets` call) instead of assuming +0. Settings that fall back to defaults count as `OcPending`
  (one +0 write on start, silent).
- **Reset settings** awaits Quit's cleanup (CPU cap cleared, boost restored, no iGPU prompt) and no longer blocks the
  UI thread (`RestoreBoostOnExit` and its `.Wait(4000)` are gone).
- **Lights**: one lighting gate for every apply and zone change, and starting an animation stops the old one (two
  Spectrum / Rainbow loops could run, one unstoppable).
- **Commands**: one gate for G-Mode, presets and power modes (F1, Home switch, Tuning radios, tray, Home tiles,
  header, F2, Auto preset, resume). F1 shares the Home switch's busy state; a second F1 during a switch toggles the
  state being switched to (never "on" twice). State re-read, status keys and OC sync mark themselves dirty and run
  again after the current run instead of being dropped (OC +0 on unplug during a write).
- **Minimized**: no sensor polling while minimized; restore starts it again.
- **Keyboard timeout**: a fade generation bumped by `Wake` (checked under the HID lock) fixes keys lit but "dark";
  `SetOff` / `Wake` send the held frame inside the lock. The 30 ms wake delay is gone.
- **Run on startup**: the task XML in %TEMP% is locked (deny write / delete) and verified while schtasks reads it. The
  task is rebuilt when its `<Command>` isn't the running exe.
- **Refresh rate** targets the internal panel (QueryDisplayConfig, shared with GpuMode), the primary display only when
  no internal panel is active.
- **Processes**: pnputil, net and powercfg run through `ProcessRunner` (timeout, stdout and stderr read together).
  pnputil is never killed: after 60 s the app reports it and keeps NVML closed until it exits.
- **UI text**: Lights hint **CTRL + ALT + F · LIGHT LEVEL** (was F7 · KEYBOARD LEVEL), Keyboard tile tooltip and
  README Zones line name Ctrl + Alt + F instead of F7.
- **Tests**: settings defaults mark `OcPending`; the startup task is rebuilt for another exe path.

## v2.0.0-beta1.2 (2026-10-23, prerelease)
Three beta1 fixes. Untested on the laptop.
- **Live strips**: Tuning strip is now CPU | GPU | PWR | FANS (fans far right, the fans value no longer runs into
  PWR), even gaps with a thin divider between each. The Home PWR strip uses the same dividers instead of dots.
- **GPU OC scope**: G-MODE and + PERF are two separate switches (new setting `OcInGMode`, default on). Either, both or
  neither; both off = no overclock at all. G-Mode toast and the Home G-Mode card follow them.
- **Open on Home**: hiding the window (F3, tray, minimize) resets it to the Home tab and closes Settings, so every
  open starts on Home.

## v2.0.0-beta1.1 (2026-10-23, prerelease)
Hotfix for beta1, which closed on launch without showing anything.
- **Fix**: the 2.0 styles file had two resources named `Card2` (a brush and the card style), so WPF threw while
  loading the theme and the app quit before the window or tray icon appeared. The unused brush is now `Card2Brush`.
- **CI**: the build now fails if any XAML file declares the same `x:Key` twice.

## v2.0.0-beta1 (2026-10-23, prerelease)
The new 2.0 look, built from the approved mockups (docs/mockups/2.0). All 1.3.5 features, new layout. The in-game
overlay comes in 2.0.0-beta2. Branch `v2.0`. Untested on the laptop.
- **Window**: one fixed 440 x 820 card with four tabs, **Home**, **Tuning**, **Lights**, **Overlay**. Every tab has the
  same height. Status line and version above the footer; footer = Run on startup, gear (Settings), Quit.
- **Home**: sensor grid (CPU / GPU temp and fans, CPU % / GPU %, RAM click to clean, VRAM) and the PWR strip, PRESET
  tiles in the preset colours (Battery locked on AC with Auto preset), G-MODE card with a switch (shows the kept GPU
  overclock), OVERLAY slider (disabled, beta 2), LIGHTING swatches + brightness.
- **Tuning**: live strip (CPU, GPU, fans, power), PERFORMANCE MODE (Battery, Quiet, Balanced, Performance, G-Mode),
  GPU MODE (iGPU only, Advanced Optimus, Ultimate), GPU OVERCLOCK with the **G-MODE / + PERF** scope pill (the 1.3.4
  "OC in Performance" setting), CPU BOOST, REFRESH and FANS side by side. Fans are **Auto / Max**.
- **Lights**: ZONES inline (Keyboard, Rear ring, Alienhead, Turn off / Turn on; was the laptop button popup), COLOR,
  EFFECT AND BRIGHTNESS, BACKLIGHT TIMEOUT (moved from Settings), STATUS KEYS.
- **Overlay**: level slider, preview and Advanced controls drawn but disabled, "Coming in beta 2". Nothing is saved.
- **Settings**: one page in the same window with two tabs, General · Keys and Alerts · Advanced. Back arrow or Esc
  returns. New rows: F4 toggles the overlay (reserved), Log frame gaps (back from 1.3.0). Run on startup is in the
  footer only.
- **Header preset button** hidden on Home; click cycles presets like F2, right click opens the preset list.
- **F3** opens / hides the app (one view now). **F4** is reserved for the overlay: a short "Overlay comes in beta 2"
  toast when notifications are on.
- **Removed**: the Fluent theme and its switch (the old setting is ignored, settings v7), the compact view and its
  footer button / F3 compact / F4 full behaviour.
- **Efficiency**: sensors and power are read only while Home or Tuning is on screen; RAM / VRAM / CPU % / GPU % only on
  Home. Hidden window = no polling (as before).
- Custom preset icon is now the sparkles icon (header button, tray menu, toasts), as in the mockups.
- Ctrl + Tab / Ctrl + Shift + Tab move between the tabs.
- App version 2.0.0-beta1.

## v1.3.5 (2026-10-22)
Fix release. 1.3.4 was built from `main`, which never got the 1.3.1 to 1.3.3 work (it lived on the `v1.3` branch), so
1.3.4 dropped the Lights menu and the other 1.3.1 to 1.3.3 changes. 1.3.5 = 1.3.3 + the 1.3.4 GPU OC features.
- Back: Lights menu (Keyboard / Rear ring / Alienhead tiles, Turn off / Turn on), Ctrl + Alt + F, restyled tray menu,
  no-flash light switching, status keys kept lit (1.3.1 to 1.3.3).
- Kept: GPU OC in Performance toggle, G-MODE + PERF hint, OC state on toasts (1.3.4).
- Pill toast: since 1.3.1 the pill is 440 wide, too narrow for the OC tag inline, so it gets its own line and the pill
  grows to 76 px tall when the tag is shown (62 otherwise).
- Lights toast (Ctrl + Alt + F) uses the app accent, the laptop's light colour, instead of the current preset's tint
  (was green under Balanced). New optional `Toast.Info.Accent`.
- Lights button (laptop icon) showed the previous state (lit with all lights off, muted with all on): the icon is now
  rebuilt on each change. Amber dot when only some lights are off is unchanged.
- beta.1 tested on the m15 R7 (2026-10-22): lighting and OC features work together.

## v1.3.4 (2026-10-21)
Stable release of 1.3.4-beta.1 to beta.4. Tested on the m15 R7 (2026-10-21): works.
- **GPU OC in Performance**: toggle with the Performance icon in the GPU Overclock header. Lit = the kept offsets also
  apply in the Performance preset on AC; dark (default) = G-Mode only. Battery always resets to +0.
- Header hint G-MODE + PERF in the lighting accent colour when lit.
- Performance and G-Mode on toasts show "OC +core / +mem" (Performance tint) or grey "OC off"; Classic card puts it on
  its own line.
- App version 1.3.4.

## v1.3.4-beta.4 (2026-10-21, prerelease)
- Classic toast: the OC tag was cut off by the narrow card ("fans up..."), so it now has its own third line under the
  detail. The Pill toast keeps it inline (wide enough). Untested.

## v1.3.4-beta.3 (2026-10-21, prerelease)
- Toasts show the GPU OC state: the Performance toast (F2) and the G-Mode on toast end with "OC +core / +mem" in the
  Performance tint when the OC will run, or a grey "OC off" when it won't (toggle dark, nothing kept, or on battery). Untested.

## v1.3.4-beta.2 (2026-10-21, prerelease)
- With the Performance toggle lit, the GPU OC header hint reads G-MODE + PERF (or ON IN G-MODE / ON IN PERFORMANCE)
  in the lighting accent colour. Dark toggle: grey G-MODE ONLY as before. Untested.

## v1.3.4-beta.1 (2026-10-21, prerelease)
- **GPU OC in Performance**: a toggle with the Performance icon at the right of the GPU Overclock header. Lit in the
  Performance tint = the kept offsets also apply in the Performance preset on AC; dark (default) = G-Mode only, as
  before. Entering or leaving Performance applies or drops the offsets; the hint reads G-MODE + PERF / ON IN
  PERFORMANCE. New setting `OcInPerformance` (false by default, no migration needed). Untested.
## v1.3.3 (2026-10-18)
- Rear ring or Alienhead on / off alone no longer turns off the five white status keys (frame re-sent, no flash).

## v1.3.2 (2026-10-18)
- Lights switch without flashing: only the light that changes is updated (keyboard over HID, ring / Alienhead with
  their own commands). Effects still use the full update.

## v1.3.1 (2026-10-18)
Stable release of 1.3.1-beta.1 to beta.4: Lights menu, Ctrl + Alt + F (full / off / 50%), tray menu with preset
icons and colors, smaller Pill (440 x 62), rounder Classic cards; beta test switches (frame-gap log, F7 test) removed.

## v1.3.1-beta.4 (2026-10-18), untested
Same as beta.3 (never tested on its own) plus a wider F7 test.
- F7 test: beta.2 only watched the collection F2..F6 send on (consumer 0x0C / 0x01) and the key hook; F7 showed in
  neither. Amine: F7 is a Fn-lock row hotkey like F2..F5, so it may go out elsewhere. The test now also watches every
  other HID collection of the Alienware keyboard (0D62) and the AW-ELC (187C) for its 20 s, and logs each report with
  its collection, plus the list of collections found.

## v1.3.1-beta.3 (2026-10-18), superseded by beta.4
Fixes from the beta.2 test.
- Fix: Keyboard off only turned off the 5 white status keys, the rest stayed lit. The rear ring / Alienhead command
  (alienfx-cli) most likely set the keyboard back to full brightness. Brightness 0 is now re-sent after every
  alienfx-cli run while the keyboard is off (also keeps the backlight timeout dark).
- The 4th tile reads **Turn off** / **Turn on** (was All off / All on). It's in the accent color while lights are on
  and plain once all are off (was the other way round).
- No focus border after clicking a tile (the Turn off tile kept a colored outline).
- F7 test result: F7 is invisible to the app (no key, no report, no state change), so the tile keeps following taps only.

## v1.3.1-beta.2 (2026-10-18), tested: Keyboard off and the All tile fixed in beta.3
Redesign after the beta.1 test (rest of beta.1 worked). Mockups: docs/mockups/1.3.1-beta.2.

**Lights menu (replaces the Off menu)**
- A laptop icon button replaces Off: accent when lights are on, an amber dot when some are off, muted when all are off.
- The menu opens above the button, right edges lined up. Esc, a click outside or a second click on the button closes it.
- A lit tile now means that light is **on**; tap Keyboard, Rear ring or Alienhead to switch it. The fourth tile reads
  **All off** (lamp with a line) while any light is on and **All on** (lamp) once all three are off. "Turn all on" removed.
- Fix: one click could switch a light on and straight back off (crash.log 21:55:15, 0.25 s apart). Taps within 350 ms
  of the last one are ignored.
- Fix: after Keyboard off then on, the 5 white status keys (F1, F12, mute, mic, right Win) stayed dark until a color
  was clicked. Turning the keyboard on now re-sends the full frame (status keys included) 150 ms later.

**Notifications**
- Classic card: 22 px corners (was 6 Legend / 9 Fluent), rounder icon tile, the Legend color bar sits inside the curve.
- Pill: 440 x 62 (was 640 x 76). Same layout, tighter: icon 40, title 16, detail 12.5, smaller switch / dots / chip.

**F7 probe**
- crash.log showed F7 sends no consumer report (the 5 logged ones were screen brightness, volume and mute).
  Settings > Advanced > **F7 test** watches for 20 s: every key down (vk / scan), every Alienware report, and the
  keyboard's lighting state read back over HID when it changes. If anything follows F7, beta.3 makes the tile follow it.

## v1.3.1-beta.1 (2026-10-18), tested: Off menu redesigned in beta.2
**New**
- Lighting > Off is a multi-select menu: Keyboard, Rear ring (chassis IDs 0 + 1), Alienhead (ID 3), All off. Any mix;
  each tile applies at once, the button shows the count ("2 off"), "Turn all on" resets. Power button (ID 2) never sent.
  Saved as `OffKeyboard` / `OffRing` / `OffAlienhead`; 1.3.0's black color migrates to all three off + cyan.

**Fixes**
- F7 after Off: the keyboard goes off through its hardware brightness (0) with frames held, the same path as the
  keyboard timeout, instead of black colors, so the firmware F7 key has colors to light. The chassis lights still get
  black. Off zones are skipped by every color, effect and animation path (no keyboard `setglobal` while it's off).
- Spectrum (100) and Rainbow wave (101) were reset to Static by the settings validation on every start.
- Keyboard timeout doesn't run while the keyboard is turned off.

**Probe**
- Any other report on the Alienware keyboard collection (not F2..F6) goes to crash.log once (max 32), to learn what
  F7 sends. Event-driven, no idle cost.

## v1.3.0 (2026-10-18)
Stable release of 1.3.0-beta.1 (confirmed on the m15 R7) and beta.2, plus the items below. Tested on the m15 R7
(2026-10-18): works.
- **Chime crackle fix**: sounds no longer go through PlaySound. Each one is handed to waveOut as one prepared buffer
  (nothing in the app refills it while it plays), opened per sound and closed when it ends (nothing stays open).
  F1 now waits 150 ms after the chime starts before the ACPI calls, like F2 already did. WAV reader is defensive
  (16-bit PCM, 1-2 channels, 8-96 kHz, max 4 MB).
  Status: fixed as far as possible for now. The crackle is much less noticeable; a faint one can still show up when a
  hardware switch (F1, F2, plug / unplug) briefly stalls the machine.

## v1.3.0-beta.2 (2026-10-17)
**New**
- Chime pack (gear > Alerts): **Soft** (new default, warm mallet + soft pad, 48 kHz) or **Classic** (the 1.2 chimes).
  Same 7 sounds, `Assets/Sounds/{Soft,Classic}`. Switching stops playback and frees the old pack's buffers; a play
  button previews Balanced, then G-Mode on.
- Toast style (gear > Alerts): **Pill** (new default) or **Classic** (the 1.2 top-right card). The pill is a ~640 x 76
  capsule at the top center of the main screen: round tinted icon tile, title + detail, G-Mode switch / F2 position
  dots / preset chip. Drops in over 220 ms, slides back up over 160 ms (slide thread); with Reduce motion or Windows
  animations off it fades instead. Test button shows a sample. Click-through, never takes focus, one window per show.
- Settings page: the gear opens a page inside the main window (card swap, back arrow or Esc), five tabs (General,
  Keys, Alerts, Backlight, Advanced), at most 5 rows each, fixed height (no scrolling, no jump between tabs).
  Dependent rows dim (toast style, chime pack, timeout options). Run on startup is in General and the footer. The last
  tab is remembered for the session. Left / Right switch tabs. Keyboard timeout is now a segmented Off / 1 / 2 / 5 / 10.
- Upgrades from beta.1 get Soft + Pill (new keys), every other value is kept. Unknown values fall back to the defaults.

**Fixes / changes since beta.1**
- Beta A/B decided on the laptop: the new slide engine stays (the 1.2 UI-thread slide is removed) and the transparent
  window stays (Normal window felt worse, removed). Both debug toggles are gone; Log frame gaps remains (Advanced).
- Gear flyout hitch gone (the popup is replaced); every slide also waits one composition before its clock starts
  (beta.1 log: 65-96 ms first-step gaps).
- Starting to the tray trims memory after 10 s (was 60 s), so the first-start peak drops to a few MB sooner.
- Checklist step 27 uses `powercfg /qh` (PROCFREQMAX is a hidden setting).

## v1.3.0-beta.1 (2026-10-17), confirmed on the m15 R7
**Fixes (code review)**
- Single instance: a second copy opens the first one's window and exits (no double tray icon / double F1 / skipped F2).
- Startup task is created from XML: no "AC power only", no "stop on battery", no 72 h limit. Old tasks are rebuilt.
- Keyboard HID: one cached handle and one lock for every frame (no interleaved frames, no SetupDi search per frame).
- The G-key hook and the macro keys run on their own input thread. schtasks and the exit clean-up are async.
- Auto preset re-checks the power line after a switch (a quick unplug + replug is no longer dropped).
- Security: `nvml.dll` only from System32; the CLIs must match alienfx-tools 9.6.x SHA-256 hashes and are held
  open while running; a warning when the app folder is writable without admin; README recommends Program Files.
- Settings: every hardware value clamped on load; atomic save (`.tmp` + replace + `.bak`, load falls back to `.bak`).
- Quit clears the Quiet 3.0 GHz cap and offers to turn the RTX back on in iGPU only.
- Ultimate detection uses Windows' BootId (Fast Startup safe) instead of the tick count.
- Resume: the preset is re-applied if the EC lost it, lighting and status keys are re-sent, findings logged.
- One HardwareController queue for both CLIs, AWCC WMI reads and BIOS WMI calls.
- Unit tests (CLI parsing, GPU mode, settings migration/clamps, Auto preset, startup XML) run in CI.
- One NVML declaration file, one "apps using the RTX" loop; every silent catch now logs a warn line to `crash.log`.
- Actions pinned to SHAs, Dependabot, `SHA256SUMS` next to each release zip. WPF-UI 3.0.5 -> 4.3.0. README GPU section: three buttons.

**New**
- Keyboard light timeout (gear): Off / 1 / 2 / 5 / 10 min, On battery only, Not in fullscreen games. Fades the
  hardware brightness, any key / mouse / touchpad input wakes it at once. Paused during Rainbow / Spectrum.
- CPU % / GPU % row under the temps (GetSystemTimes; RTX 3D engine from Windows' GPU Engine counters, no NVML). Off in iGPU only.

**Animations**
- New slide engine: a short-lived thread steps every slide once per composition (DwmFlush), placed for the vblank it
  shows on, with async SetWindowPos. Drives open/close, minimize, the card swap and the toasts (now a slide, not a fade).
- Softer open curve (0.25, 1, 0.5, 1) at 280 ms; layout done off screen; no GC during a slide; storyboards at the
  panel's refresh rate (was 60 fps); follows Windows' Animation effects too.
- Debug toggles (gear > Debug): New slide engine, Normal window (rounded corners + native shadow, restart), Log frame gaps.

## v1.2.0 (2026-10-17), confirmed on the m15 R7
Stable release of the 1.2.0 betas (all tested on the laptop):
- **New toasts + chimes**: card toasts (tinted icon tile, title + detail, F2 pills / G-Mode switch / plug chip), both
  themes, and a soft chime per preset (no crackle).
- **iGPU only** GPU mode: turns the RTX off with `pnputil` (Advanced Optimus + NVIDIA Optimus only). It lists the apps
  using the RTX first, locks Ultimate while it's off, and Advanced Optimus brings the RTX back live (NVIDIA tray
  recovered). HDMI / Mini DP stay dark while it's off. The app no longer closes when switching.
- Lock badge on iGPU only and Battery when they're locked; the "why" note shows when you click the locked iGPU only button.
- Compact <-> full switch is a card swap; snappier open/close; no PC freeze when spamming F3/F4; F2 presses don't stack.
- Shows as "Alien Controls" in startup lists; the startup task is renamed (the old one is migrated).
- Security & Privacy: Windows tools are run by their full System32 path. Still no network access at all. There's a local
  crash log in `%AppData%\AlienControl`.

## v1.2.0-beta.5 (2026-10-17), confirmed
- Compact <-> full is now a card swap: the window slides fully down past the edge, switches out of sight, and slides
  back up in its new size (the 40 px dip showed the height jump on screen).
- Fix: spamming F3/F4 (or the tray) could freeze the PC for a moment. Each open ran alienfan-cli twice plus the
  AWCC/BIOS ACPI reads, and quick open/close stacked them. Now one refresh at a time, and the CLI/BIOS reads are
  skipped when the last ones are under 20 s old (sensors still read on every open).
- F2 presses while a preset is still being applied are ignored instead of queuing up.

## v1.2.0-beta.4 (2026-10-17), confirmed
- Fix: the app closed when switching to iGPU only. The power meter kept an NVML handle to the RTX and read it while
  pnputil removed the device. NVML is now closed first (power timer paused), GPU power and OC stay off while the RTX
  is disabled, and NVML comes back ~4 s after the RTX is re-enabled.
- The amber "why it's locked" note under GPU Mode only shows when you click the locked iGPU only button (6 s).
  The green "RTX is off" note stays.
- Small lock badge in the top-right corner of iGPU only and Battery when they're locked.
- Local crash log (`%AppData%\AlienControl\crash.log`) for UI / background exceptions. Never sent anywhere.

## v1.2.0-beta.3 (2026-10-17), confirmed
- Compact <-> Full menu now switches with a native "dip and rise": the window drops 40 px (110 ms), changes view,
  then rises into its new size (200 ms). Footer button and F3/F4. Instant with Reduce motion.
- Snappier open/close: the memory trim runs 60 s after hiding (was 3 s, so a quick reopen had to page everything
  back in) and is lighter (no forced compacting GC). The open slide starts one frame sooner, hide is 150 ms (was 170),
  and F3/F4 skip the foreground calls when the window is already focused.
- Chimes: no more crackle at the end of notes (smooth 80 ms release + final fade), 40 ms of silence first so a sleeping
  audio device doesn't pop, and F2 waits 150 ms after the chime starts before switching the hardware.

## v1.2.0-beta.2 (2026-10-17), confirmed
- **iGPU only is back** as a third GPU Mode button (left of Advanced Optimus). It disables the RTX with `pnputil`
  (survives reboots); HDMI / Mini DP go dark until it's back on.
  - Works only in Advanced Optimus with NVIDIA set to **Optimus**. On **Automatic Select** it's pressable but shows
    "Switch to Optimus first" (Open NVIDIA panel / OK). Locked with a note in dGPU only and Ultimate.
  - Before turning the RTX off: "Turn off the RTX?" lists the apps holding RTX memory (with MB) - Cancel / Refresh /
    Turn off anyway. Nothing on it = a short Cancel / Turn off.
  - While the RTX is off: green note under the row, hint "iGPU only · RTX off", **Ultimate is locked**.
  - Advanced Optimus turns the RTX back on right away (no restart). If NVIDIA's tray icon doesn't come back in ~6 s,
    the app restarts NVIDIA's Display Container service. Kept GPU OC offsets are re-applied (G-Mode on AC).
  - Plugging in never turns the RTX back on; it stays manual.

## v1.2.0-beta.1 (2026-10-17), confirmed
- New toasts in the Dolby Controls style: a 44 px icon tile in the preset's color, title + detail line, and the state on
  the right (Quiet / Balanced / Performance pills for F2, a switch for F1 G-Mode, a preset chip for plugged in / on
  battery). Legend and Fluent. Same top-right spot and timing.
- New soft bell chimes: one note per F2 preset (low to high), G-Mode on three notes rising, off two falling, plug two
  notes up, unplug the same two down. The old tick is gone. Generator: dev/chimes.py.
- Includes the 1.1.1 name fix.

## v1.1.1 (2026-10-16), untested
- Startup apps / Task Manager now show "Alien Controls" (with a space) instead of "AlienControl" (exe file description).
- The run-on-startup task is now called "Alien Controls". An old "AlienControl" task is swapped for the new one
  automatically on first start (same settings: highest privileges, at logon, `--tray`).

## v1.1.0 (2026-10-16), stable
Everything from the 1.1 betas, all confirmed on the m15 R7:
- Compact view: tray and F3 open compact, F4 / footer button the full menu (beta.3).
- Temps and fans read straight from AWCC WMI, CLI fallback (beta.1).
- Show in taskbar toggle, off = tray only (beta.4).
- Header preset button + preset list instead of the chip (beta.5).
- Fluent Acrylic (beta.2) was tried and dropped.

## v1.1.0-beta.5 (2026-10-16), confirmed
- The header preset chip is gone. A header button (left of minimize) shows the active preset's icon in its color;
  clicking it opens a list of Battery, Quiet, Balanced, Performance and Custom, each with icon + color, the active one
  marked. Both themes. Battery is greyed on AC with Auto preset, like the mode button.

## v1.1.0-beta.4 (2026-10-16), confirmed
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
