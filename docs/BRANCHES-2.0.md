# 2.0 branches: who works where

Two lines of work run in parallel until 2.0.0-beta2. Read this before you push.

| Branch | Based on | Owner | What goes in | Tags |
|---|---|---|---|---|
| `main` | 1.3.x | nobody right now | untouched until 2.0 is stable | `v1.3.*` |
| `v2.0` | 2.0.0-beta1.3 | agent A | beta1.4 and later fixes: Tier 1, 2, 3 review work, WPF-UI removal, MainWindow partials, startup task security | `v2.0.0-beta1.4`, ... |
| `beta1.3-ov` | 2.0.0-beta1.3 (`1c26220`) | agent B | the in-game overlay only (PresentMon frametimes, 4 SteamOS levels, F4, Overlay tab) | `v2.0.0-beta1.3-ov.N` |

## Rules
- Agent A never pushes to `beta1.3-ov`. Agent B never pushes to `v2.0` or `main`.
- `beta1.3-ov` does **not** get the beta1.4 fixes. Its builds are for testing the overlay only. Install them over beta1.3, not over beta1.4.
- Overlay test builds use tags `v2.0.0-beta1.3-ov.1`, `.2`, ... (pre-release). They never use `v2.0.0-beta1.4` or `v2.0.0-beta2`.
- To keep the merge easy, overlay code lives in its own files:
  - `src/AlienControl/Overlay/` (window, levels, drawing)
  - `src/AlienControl/Services/Frames*.cs` (PresentMon source)
  - `src/AlienControl/MainWindow.Overlay.cs` (Overlay tab, F4)
  - Small hooks only in existing files (settings keys, tab wiring, exit cleanup). Every hook is marked `// beta1.3-ov`.
- `tools/PresentMon/` holds the PresentMon CLI (MIT, GameTechDev). It is a separate exe, no injection.

## Merging into 2.0.0-beta2
Whoever merges (agent A or B):
1. Wait until beta1.4 is merged into `v2.0` and the overlay is signed off by Amine on `beta1.3-ov`.
2. Branch `merge/beta2` off `v2.0`, then `git merge beta1.3-ov`.
3. Expected conflicts: `MainWindow.xaml.cs` (split into partials in 1.4: move each `// beta1.3-ov` hook into the matching partial), `Settings.cs`, `AlienControl.csproj` (WPF-UI removed in 1.4: overlay must not use WPF-UI), `CHANGELOG.md`, `build.yml` (drop `beta1.3-ov` from the branch list).
4. Grep `beta1.3-ov` after the merge: every hook must still be wired.
5. Bump to `2.0.0-beta2`, release notes `docs/releases/v2.0.0-beta2.md`, PR into `v2.0`, Windows CI green, tag `v2.0.0-beta2`.
6. Delete `beta1.3-ov` after the release.

## What beta1.3-ov adds (for the merge)
New files (no conflicts expected):
- `src/AlienControl/Services/FramesSource.cs` (PresentMon child, CSV parse, job object, swapchain filter, `Playhead` graph playback), `Services/CpuCores.cs`, `Services/VblankCounter.cs`
- `src/AlienControl/Services/NvDrs.cs` (NVAPI DRS: per game FPS cap + V-Sync, read back), `MainWindow.FpsCap.cs` (cap chips + APPLY on the Overlay tab)
- `src/AlienControl/Overlay/OverlayLevels.cs`, `OverlayPanel.cs` (renderer), `OverlayHost.cs` (window, own thread), `LevelSlider.cs`
- `src/AlienControl/MainWindow.Overlay.cs` (levels, F4, Overlay tab, feeding sensors, exit cleanup)
- `tests/AlienControl.Tests/OverlayTests.cs`, `docs/releases/v2.0.0-beta1.3-ov.*.md`

Hooks in existing files (all marked `beta1.3-ov`):
- `MainWindow.xaml.cs`: `InitOverlay()` in the constructor; `RefreshSensorsAsync` / `RefreshPowerAsync` also run for the overlay and call `OverlayTakeSensors` / `OverlayTakePower`; hidden window calls `UpdatePolling()` instead of stopping the timers; `AllowClose` calls `StopOverlayOnExit()`. In 1.4 these live in the Sensors / Window partials.
- `MainWindow.Tabs.cs`: `UpdatePolling()` keeps the poll / power timers for the overlay's needs; beta1's `OnF4Pressed` hint removed.
- `MainWindow.Oc.cs`: `EmergencyExitCleanup` stops PresentMon.
- `MainWindow.xaml`: Home overlay block and the whole `OverlayPage` replaced; `xmlns:ov`.
- `Services/Settings.cs`: `Overlay*` keys + clamps. `Services/Nvml.cs`: `nvmlDeviceGetClockInfo`.
- `AlienControl.csproj`: `ConcurrentGarbageCollection` false + `System.GC.ConserveMemory` 5 (ov.11 RAM). Keep both in the merge.
- `Themes/Legend2.xaml`: `Icon.Gauge`. Settings > General: Background slider (all overlay levels).
- `.github/workflows/*.yml`: PresentMon download step (pinned SHA256). `.gitignore`: `tools/PresentMon/`.
- 1.4 moves GPU % to one PDH query and removes WPF-UI: the overlay uses neither directly, it reads `_stats.GpuUsage()` through `RefreshSensorsAsync`.

## Where beta1.3-ov is (2026-10-24)
- Latest: `v2.0.0-beta1.3-ov.14` (`0c5d26f`), CI green. Not signed off yet, don't merge.
- ov.11: NVIDIA driver FPS cap + V-Sync per game, background slider moved to Settings, swapchain filter, RAM teardown 30 s after F4 off.
- ov.12: Size / Move X / Move Y as compact steppers in Overlay Advanced.
- ov.13: FPS cap APPLY button, profile name + exe shown, read-back + relaunch check. Tested OK on Amine's 3070 Ti.
- ov.14: smooth frametime graph (plays PresentMon bursts back), FPS over the last 250 ms.
- Next, ov.15: clock row, CPU/GPU bound tile, stutter indicator. Later: DirectComposition + Direct2D overlay window.
