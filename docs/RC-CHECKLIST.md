# 1.0.0 release checklist (m15 R7)

Tick every line on the laptop. Any ✗ goes back as a fix before 1.0.0.

## 1. Start + tray
- [ ] Fresh start: UAC, window opens; footer shows `v1.0.0-rc1`
- [ ] Reboot: starts hidden in the tray (Run on startup on), no UAC prompt
- [ ] Tray left click opens / slides back; right-click menu themed, accent text
- [ ] Tray icon tint follows the preset

## 2. Presets (window, tray, F2)
- [ ] Battery: 60 Hz, boost Off
- [ ] Quiet: 240 Hz, Efficient (CPU capped ~3.0 GHz)
- [ ] Balanced: 240 Hz, boost On
- [ ] Performance: 240 Hz, Aggressive
- [ ] Custom changes nothing; profile chip follows every change
- [ ] F2 cycles Q → B → P on AC (F2 key stays the keyboard color), P → Battery → Q on battery; F2 does nothing in G-Mode
- [ ] No preset ever changes the GPU mode

## 3. G-Mode + OC
- [ ] F1 toggles G-Mode: fans max, white chip, F1 key lit
- [ ] OC test (+200 / +200): apply, 20 s keep-or-revert, Keep → reapplied next G-Mode on AC
- [ ] Leaving G-Mode / unplugging puts OC back to +0
- [ ] Quit while OC is on: OC back to +0

## 4. Auto preset (AC / battery)
- [ ] Unplug → Battery (leaves G-Mode first), toast + sound
- [ ] Plug in → previous preset (after G-Mode / Custom → Balanced), toast + sound
- [ ] Battery greyed on AC

## 5. Fans, sensors, power
- [ ] Temps / RPM update while open, stop when hidden
- [ ] Fan boost slider works
- [ ] CPU / GPU / total watts sensible on AC and battery

## 6. Lighting + status keys
- [ ] Static color on keyboard + chassis (ring, lid), power button untouched
- [ ] Effects (breathing, wave, …) and brightness
- [ ] Speaker mute, mic mute, touchpad (F12) keys follow instantly, also after switching audio device
- [ ] Color comes back after reboot

## 7. GPU mode
- [ ] Optimus hint shows NVIDIA mode + panel GPU
- [ ] Switch to Ultimate → restart prompt → boots in Ultimate; switch back works
- [ ] "RTX in use" dialog when relevant

## 8. Display
- [ ] 60 / 240 Hz buttons; resolution kept

## 9. F3 + notifications
- [ ] F3 opens / hides; brings the window up over a borderless game
- [ ] Toasts top-right, right size at 125%, both themes; sound toggle independent

## 10. Settings
- [ ] Both themes look right (window, gear menu, tray menu, dialogs, toasts)
- [ ] Reduce motion: everything instant
- [ ] Every gear toggle survives a restart
- [ ] Reset settings: defaults + restart, startup / GPU mode untouched
- [ ] About line shows `1.0.0-rc1`

## 11. Idle cost
- [ ] Tray: 0% CPU, a few MB RAM after ~5 s
- [ ] Open idle: ~20 MB, ~0% CPU
- [ ] An hour in the tray: no growth, no crash
