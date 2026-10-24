# Alien Controls 2.0 design handoff

Picked on 2026-10-23: **Home A, Tuning A, Lights A**, the fitted Overlay tab, and the two tab Settings page.
Theme: **Legend only** (Fluent is gone). No em dashes or en dashes anywhere in the UI.

## Files in this handoff

| File | What it is |
|---|---|
| 00-sheet-app.png | Home, Tuning, Lights side by side |
| 00-sheet-overlay-settings.png | Overlay tab and both Settings tabs side by side |
| 01-home.png | Home (A, classic) |
| 02-tuning.png | Tuning (A, 1.3.5 order) |
| 03-lights.png | Lights (A, zones on top) |
| 04-overlay.png | Overlay tab (level slider, preview, Advanced) |
| 05-settings-general-keys.png | Settings, tab 1 |
| 06-settings-alerts-advanced.png | Settings, tab 2 |
| 07-overlay-level-1..4.png | The in-game overlay at levels 1 to 4 (SteamOS / MangoHud style) |

## Release plan

- **2.0.0-beta1**: the new look only. All 1.3.5 features, new layout. The Overlay tab is present but marked "Coming in beta 2" (no PresentMon yet). F4 is reserved for the overlay and does nothing visible yet.
- **2.0.0-beta2**: the overlay itself (PresentMon ETW, click through window, 4 levels, Advanced settings).

## Global rules

- Window **440 x 820**, every tab exactly the same height. No dead space: spare height goes into the tiles (flex), gaps are 18 px.
- **Compact / mini mode removed.**
- **F3 opens the app** (press again to hide). **F4 toggles the overlay** off or back to the last level used.
- Fans are **Auto** and **Max** only (no fan slider).
- Efficiency: only read what is on screen. When the window is hidden, sensor polling for the window stops. App and overlay share the same pollers (never read the same sensor twice).

## Shell (every tab)

- Header: Alien logo, ALIEN CONTROLS, model chip (m15 R7), **preset button** (colored icon of the current preset, click cycles like F2), minimize, close.
  - The preset button is **hidden on Home** (presets are already on the page) and shown on Tuning, Lights, Overlay.
- Tabs: HOME, TUNING, LIGHTS, OVERLAY with the cyan underline on the active one.
- Status line above the footer (last action, left) and version (right, mono).
- Footer: Run on startup toggle, gear (opens Settings), Quit.

## Home (A)

1. **Sensor grid** (as 1.3.5): CPU / GPU temp, fans, use, RAM (click to clean), VRAM, plus the **PWR strip** (CPU W, GPU W, total).
2. **PRESET** (F2): Battery, Quiet, Balanced, Performance, Custom. Each has its color (amber, green, cyan, red, violet). Battery locked while on AC, as today.
3. **G-MODE** (F1): card "Fans max, aggressive boost" / "GPU overclock +150 / +500 on AC" with a toggle.
4. **OVERLAY**: Steam style slider OFF 1 2 3 4, note "F4 · LEVEL n". In beta1 it is disabled with "beta 2".
5. **LIGHTING**: 5 swatches + brightness slider with %.

## Tuning (A)

1. **Live strip**: CPU temp, GPU temp, fans %, PWR.
2. **PERFORMANCE MODE**: Battery (locked on AC), Quiet, Balanced, Performance, G-Mode.
3. **GPU MODE**: iGPU only (locked when not available), Advanced Optimus, Ultimate. Note shows current mode and GPU.
4. **GPU OVERCLOCK**: Core and Memory sliders (0 to 300 MHz), Reset, "ACTIVE +x / +y", Apply.
   - **Scope pill in the section header: `G-MODE` / `+ PERF`**. G-MODE = overclock only runs in G-Mode (1.3.5 "G-mode only"). + PERF = also runs in the Performance preset (the old gauge button). Same setting as 1.3.4/1.3.5, new look.
5. **CPU BOOST** (AC): Off, On, Efficient, Aggressive.
6. **REFRESH** (60 / 240 Hz) and **FANS** (Auto / Max) side by side.

## Lights (A)

1. **ZONES** (was the 1.3.5 popup, now inline): Keyboard, Rear ring, Alienhead, divider, **Turn off**. Lit tile = zone on, tap to switch. Turn off kills all zones.
2. **COLOR**: 5 swatches (cyan, violet, red, green, white). The app accent follows.
3. **EFFECT AND BRIGHTNESS**: FX dropdown (Static, Breathing, Spectrum, Rainbow wave, Pulse, Wave) + brightness slider with %. Hint: CTRL + ALT + F · LIGHT LEVEL (F7 is not used; changed in 2.0.0-beta1.3). beta1.4 visual tweaks: see `../2.0-beta1.4/`.
4. **BACKLIGHT TIMEOUT** (moved here from Settings): Off / 1 / 2 / 5 / 10 min, On battery only, Not in fullscreen games.
5. **STATUS KEYS**: info line, G-Mode, mute, mic and touchpad keys light up white.

## Overlay tab

1. **OVERLAY LEVEL** slider OFF 1 2 3 4 (F4).
2. **Preview** of the selected level (flex height).
3. **ADVANCED**: position (6 spots, default top left like SteamOS), size, background opacity (default 50%), update every 250 / 500 / 1000 ms, customize level (1 to 4) checklist: GPU load+temp, CPU load+temp, clocks, power, VRAM and RAM, battery, frametime graph, per thread CPU, fans, refresh rate. "RESET TO STEAMOS".
4. Status line: "Only what is on screen gets read. Off = nothing runs".

### Overlay levels (beta 2, match MangoHud presets 1 to 4)

- Level 1: FPS only.
- Level 2: horizontal bar: FPS, frametime, CPU / GPU load, RAM, VRAM, battery, power.
- Level 3: vertical: GPU, CPU, VRAM, RAM, BATT, engine (DX12) + FPS, frametime graph.
- Level 4: level 3 + per thread CPU, clocks, fans, refresh rate, detailed frame timing.
- MangoHud colors: GPU #2E9762, CPU #2E97CB, VRAM #AD64C1, RAM #C26693, BATT #FF9078, engine #EB5B5B, frametime #00FF00, background #020202 at 50%, Ubuntu font, square corners.

## Settings (same 440 x 820 window, back arrow / Esc returns)

**Tab 1: GENERAL · KEYS**
- Show in taskbar (off = tray only)
- Reduce motion
- Auto preset (Battery when unplugged, last preset back on AC)
- G-key (F1) toggles G-Mode + Detect key
- F2 cycles presets
- F3 opens the app
- F4 toggles the overlay

**Tab 2: ALERTS · ADVANCED**
- Notifications (pill toast for F1, F2, F4 and plug / unplug)
- Toast style Pill / Classic + Test
- Notification sound
- Chime pack Soft / Classic + play
- Log frame gaps
- Reset settings (defaults, then restart)
- Credits: alienfx-tools (T-Troll, MIT), PresentMon (Intel, MIT), not made by Dell.

Run on startup lives in the footer only.

## Colors (Legend)

BG #090A0C, card #0E1013, line #1C2127 / #16191E, text #F2F6F9, muted #A9B4BF, faint #6E7A86, accent #00E5FF, purple #7C4DFF, selected bg #0A1A1F. Fonts: Barlow (headings, caps), Inter (body), Cascadia Mono (numbers, notes).
