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
