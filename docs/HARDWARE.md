# Hardware findings: Alienware m15 R7

Everything here was measured on the real laptop (i7-12700H, RTX 3070 Ti Laptop GPU, 32 GB DDR5-4800,
1440p 240 Hz panel at 125% scaling, auto-hide taskbar) with T-Troll's tools, run as Administrator from `cmd.exe`.
Tool versions used: alienfan-cli 9.6.0.1, alienfx-cli 9.6.2.

## Fans, power and sensors (alienfan-cli)

Probe: systemID 83886594, 2 fans, 13 sensors, 6 power states, G-Mode, XMP. No TCC, no ACPI lights.
`setgpu` (GPU power limit) is **not** in this version's command list, so the GPU limit UI was removed in v0.2.

### Power modes
`getpower` prints the raw BIOS value (`Power mode: Level 160 (a0)`), while `setpower` takes an **index**:
index 0 is manual/unlocked, then the 5 BIOS values. Confirmed with `setpower=N` followed by `getpower`:

| Index | Raw | Mode | Used by the app as |
|---|---|---|---|
| 1 | a0 | Balanced | Balanced |
| 2 | a1 | Balanced-Performance | Performance |
| 3 | a5 | Low Power / Battery Saver | Battery |
| 4 | a3 | Quiet | Quiet |
| 5 | a4 | Performance (behaves like G-Mode, F1 key lights white) | not used |

Mode names are inferred from the Linux `alienware-wmi` thermal-mode constants. Index 5 acted like G-Mode, so Performance
was moved to index 2 in v0.5. Restore a stuck state with `setgmode=0 setpower=1`.

### Sensors and fans
- Temperature sensors: `CPU Internal Thermistor`, `GPU Internal Thermistor`. Fans: `Fan CPU 0 RPM`, `Fan GPU 1 RPM`.
- `maxrpm` reports 5000 for both fans (5004 was seen under load). The app shows `rpm / 5000`, clamped to 100%.
  alienfan's own `percent` output doesn't equal rpm/max exactly.
- Fans reading 0 rpm at idle is real: the laptop sits on a cooling pad, and the CLI agrees.
- Max boost works (`Fan GPU 1 boost set: 100`).

### CPU boost
`setperf=<ac>,<dc>` changes the Windows power plan's `PERFBOOSTMODE`. Verified with `powercfg /qh`: AC index 0 with Off,
2 with Aggressive (DC stayed 3). The active scheme was High performance.

### G-key
The F1 G-key arrives as virtual key **0x80 (F17)** and reaches a normal low-level keyboard hook (`WH_KEYBOARD_LL`).
No WMI listener is needed. The hook is always on since v0.8.

### WMI
`alienfan-cli dump` of `AWCCWmiMethodFunction` exposes methods 13-37, 128-136 (Caldera) and 160-164 (Tobii).
There is **no** MUX/GPU-switch method in the AWCC WMI, so MUX (Ultimate) has to go through Dell BIOS WMI
(`root\dcim\sysman\biosattributes`). The exact attribute name is still to be confirmed from the in-app
"Check Ultimate support" report.

### XMP
The probe lists XMP, but the stock memory is JEDEC DDR5-4800 with no XMP profile, so an XMP toggle does nothing here. Dropped.

## Lighting (alienfx-cli)

`alienfx-cli probe` finds no Dell API, but two low-level devices:

| Device | USB ID | API | What it is |
|---|---|---|---|
| 0 | VID 0d62 / PID cabc "DELL Technologies Keyboard" | APIv5 | per-key RGB keyboard, IDs 0-135 |
| 1 | VID 187c / PID 0550 "Alienware AW-ELC" | APIv4 | chassis lights |

### Chassis (device 1)
The names saved by `probe`/`status` are **wrong**. Mapped by lighting one ID at a time:

| ID | Light |
|---|---|
| 0 + 1 | rear "tron" ring (driven as one light, like AWCC) |
| 2 | **power button: never send** (the firmware's blue on AC / amber on battery is kept on purpose) |
| 3 | lid Alienhead |
| 4-15 | no visible effect |

`setdim` on device 1 also dimmed the power button and never reached 0 on the ring, so it isn't used.

### Keyboard (device 0)
- IDs don't follow physical key order and the saved probe names are unreliable. Esc = 0.
- Firmware status keys (the key shows its normal color when off, white when the feature is on):
  F1 G-Mode = 1, F12 touchpad lock = 12, audio mute = 16, mic mute = 19, right Win-lock = 109.
- APIv5 global effect types for `setglobal`: 0 Color, 2 Breathing, 3 Single wave, 4 Dual wave, 8 Pulse,
  9 Mixed pulse, 10 Night Rider, 11 Laser. There is no firmware spectrum on APIv5.

### The status-key bug and its root cause
Symptom: after any color change, the five status keys went dark until a reboot.

What the tests showed, in order:
1. Any single `setone` on device 0 (even Esc) turned all five off: the keyboard leaves firmware mode.
2. Chained `setone 0,1` + `0,16`: only 16 (the last one) lit.
3. Separate calls `setone 0,1` then `0,12`: only 12 lit, and keys that were never written went dark.
4. `setall` lit several status keys at once (one multi-light packet) but reset brightness to full.

Root cause: **APIv5 firmware keeps status-key colors only from the most recent frame**, every CLI `setone` is a frame of
its own, and a frame also resets brightness. Fix (v0.5.2): `Services/KeyboardHid.cs` writes all 136 keys in **one HID
feature-report frame** (report id 0xcc, same bytes as T-Troll's SDK). Never go back to per-key `setone` for the keyboard.

Brightness: the hardware dim command (turnOnSet) only flashed the keyboard and `setdim` hit the power button, so brightness
is done in software (colors scaled by level/255) since v0.5.3. At startup the app resets hardware dim to 255 to undo older builds.

## Display and GPU
- WPF rendering at 125%: an Effect (DropShadow) on a parent of text forces bitmap rendering and kills ClearType, so text looks soft. Keep effects on empty layers only, and keep `UseLayoutRounding` on.
- Internal panel: 1440p, 60 and 240 Hz modes. The app switches with `ChangeDisplaySettingsEx` (±5 Hz match, resolution kept).
- NVIDIA Advanced Optimus "Laptop display mode" offers Automatic Select / NVIDIA GPU only / Optimus and switches the panel
  without a reboot (DDS). NVIDIA states there is **no public API** for it, so a Procmon recording of the switch is planned
  (see ROADMAP.md).
- In Optimus the Intel iGPU already drives the internal panel and the RTX sleeps when unused.
- The BIOS (and AWCC) also offer the MUX / hybrid graphics setting, which needs a restart.
