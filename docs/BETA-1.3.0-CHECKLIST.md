# 1.3.0-beta.1 test checklist (m15 R7)

One session, top to bottom. Report back by step number: ✓, ✗ + what happened, or "?" if unsure.
Before you start: Quit the old version, move the folder to `C:\Program Files\Alien Controls` (exe + the two CLIs), start the new exe.
The log is `%AppData%\AlienControl\crash.log` (send it with any ✗).

## A. Start, install, single instance
1. Start the exe. Expected: UAC once, window opens compact, footer shows `v1.3.0-beta.1`, no "Heads-up: this folder…" status line.
2. (Optional) Start a copy from a folder like `C:\Tools`. Expected: status line "Heads-up: this folder can be changed without admin rights…". Quit it, back to Program Files.
3. With the app running, double-click the exe again. Expected: the running window opens; still ONE tray icon.
4. Press F1 twice (G-Mode on, off) and F2 once. Expected: one toggle per press, F2 moves exactly one preset.
5. Gear > Run on startup: turn off, then on. Expected: no UI freeze while it switches.
6. Task Scheduler > Task Scheduler Library > **Alien Controls** > Properties. Expected: Conditions tab: "Start the task only if the computer is on AC power" UNticked. Settings tab: "Stop the task if it runs longer than" UNticked.
7. Unplug, reboot, log in on battery. Expected: the app starts in the tray (Auto preset puts Battery on).
8. Plug in and unplug a few times quickly (within ~2 s). Expected: it always ends on the right preset for the final state (Battery unplugged, your AC preset plugged in).

## B. CLIs and security
9. Normal use (presets, G-Mode, lighting). Expected: everything works, no "isn't a known alienfx-tools 9.6 build" message. If you see it: send the crash.log line with the hash.
10. Gear > About / footer: nothing new on the network side (still zero network). Expected: Windows firewall shows no prompt ever.

## C. Keyboard and status keys
11. Static color, toggle speaker mute, mic mute and touchpad (F12) several times fast. Expected: the right status keys light, no flicker or wrong colors.
12. Pick Spectrum, then press mute while it runs. Expected: animation keeps going, mute key white, no flicker.
13. Press F1 rapidly while a game (or any app) is busy. Expected: every press registers; F1 keeps working all session.

## D. Keyboard light timeout (new)
14. Gear > Keyboard light timeout = 1 min. Don't touch anything for ~1 min. Expected: keys fade out smoothly (~0.6 s).
15. Press any key. Expected: keys back immediately at your color/brightness, status keys correct.
16. Repeat 14, then move the mouse. Then repeat, then touch the touchpad. Expected: both wake it at once.
17. While dark, press mute (wakes it), check the mute key is lit correctly.
18. Turn on "On battery only", plugged in, wait 1+ min. Expected: no fade. Unplug, wait 1+ min. Expected: fades.
19. Start a fullscreen game with "Not in fullscreen games" on, idle 1+ min (controller or just watch). Expected: keys stay lit.
20. With Spectrum / Rainbow running, idle 1+ min. Expected: no fade.
21. Set it back to Off. Expected: never fades.

## E. CPU % / GPU % row (new)
22. Open the window. Expected: new row CPU usage · GPU usage between the temps/fans and RAM/VRAM, in compact and full, both themes.
23. Run a game or GPU load. Expected: GPU % rises (close to Task Manager's 3D for the RTX), CPU % matches Task Manager roughly.
24. On battery with nothing on the RTX, open the window a few times. Expected: the RTX stays asleep (NVIDIA tray's GPU activity shows no app; GPU power ~0 W).
25. iGPU only on. Expected: GPU usage shows "Off".

## F. GPU mode, exit, sleep
26. iGPU only on, then tray > Quit. Expected: window opens with "The RTX is still off" (Keep it off / Turn it on). Pick Turn it on. Expected: RTX back, app exits.
27. Quiet preset, then Quit. Then run `powercfg /qh scheme_current sub_processor PROCFREQMAX` and `powercfg /qh scheme_current sub_processor PROCFREQMAX1` (`/qh`, the setting is hidden). Expected: AC and DC both 0x00000000.
28. Switch Advanced Optimus -> Ultimate, but choose Restart later, then Start > Shut down (not restart), power on. Expected: GPU row shows Ultimate correctly (not swapped). Switch back the same way.
29. Sleep the laptop 1+ min, wake. Expected: preset, lighting and status keys are as before. crash.log has a "Resume:" line (send it either way).

## G. Animations (the main event)
30. Gear > Debug: New slide engine ON (default). Open/close with F3 and the tray 10 times. Expected: smooth at 240 Hz, softer start than 1.2, no stutter.
31. Compact <-> full with F4 and the footer button. Expected: smooth card swap, never a visible height jump.
32. Minimize, then the gear flyout. Expected: both smooth.
33. F2 / plug toasts. Expected: the toast slides down from the top and back up (no fade).
34. Battery preset (60 Hz): repeat 30. Expected: still smooth (60 steps/s).
35. Gear > Debug: Log frame gaps ON, do 30 + 31 again, then turn it off. Send the "Slide" lines from crash.log (target: ~240 steps/s at 240 Hz, biggest gap under ~8 ms).
36. New slide engine OFF: repeat 30 + 31. Tell me which one feels better.
37. Normal window ON, then Quit and start again. Expected: rounded corners, native shadow, everything else the same. Repeat 30-33. Tell me which window feels better (and any glitch: black corners, double shadow, Windows' own animation).
38. Normal window OFF, restart. Windows Settings > Accessibility > Visual effects > Animation effects OFF. Open the app. Expected: no slides (instant). Turn it back on.
39. Spam F3/F4 open/close 20 times. Expected: no PC freeze.

## H. Themes (WPF-UI 4.3 bump)
40. Legend and Fluent: check every section, tooltips, combo boxes (effect, keyboard timeout), gear menu, preset popup, dialogs. Expected: same look as 1.2, nothing unstyled or missing icons.

## I. 1.2 regressions
41. iGPU only on / off, Advanced Optimus brings the RTX back with NVIDIA's tray icon. Expected: as in 1.2.0.
42. G-Mode OC on AC (+200/+200), keep/revert test. Expected: as in 1.2.0.
43. Idle cost: window closed for 2 min. Expected: 0% CPU, a few MB in Task Manager; window open idle ~20-40 MB, 0% CPU.
