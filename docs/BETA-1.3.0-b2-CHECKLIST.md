# 1.3.0-beta.2 test checklist (m15 R7)

Only what's new since beta.1. One session, top to bottom. Report by step number: ✓, ✗ + what happened, or "?".
Before you start: Quit beta.1, replace the exe in `C:\Program Files\Alien Controls` (keep the two CLIs), start it.
Log: `%AppData%\AlienControl\crash.log` (send it with any ✗).

## A. Upgrade
1. Start the exe. Expected: footer shows `v1.3.0-beta.2`; your theme, presets, lighting, keyboard timeout and toggles are the same as in beta.1.
2. Gear > Alerts. Expected: Toast style = **Pill** and Chime pack = **Soft** (new defaults, also for upgrades).

## B. Settings page
3. Click the gear. Expected: the window slides down and comes back up as the Settings page (back arrow, five tabs: General, Keys, Alerts, Backlight, Advanced). Smooth, no flash of the old view.
4. Click each tab (layout: [settings mockup](mockups/1.3-beta2/settings.png)). Expected: the window height never changes, nothing scrolls, nothing is cut off. Both in compact and full view.
5. Check every old setting is there and works: General (Theme, Run on startup, Show in taskbar, Reduce motion, Auto preset), Keys (Detect key, F2 cycles presets, F3 / F4), Alerts (Notifications, Notification sound), Backlight (timeout, On battery only, Not in fullscreen games), Advanced (Log frame gaps, Reset settings, version line).
6. Toggle Run on startup on the page, go back. Expected: the footer switch matches (same setting), and the other way round.
7. Keys > Detect key, press F1. Expected: the line shows "Key 0x80" (or your key), F1 still toggles G-Mode.
8. Back arrow, then gear again, then Esc. Expected: both return to the main view. Gear again: it reopens on the last tab you used. Quit and restart: it opens on General.
9. Keyboard: on the page press Tab a few times, and Left / Right on the tabs. Expected: focus moves through the controls, Left / Right switch tabs, Esc goes back.
10. Alerts: turn Notifications off. Expected: the Toast style row dims and can't be clicked; the Notifications subtitle says "Off: no toasts". Turn it back on.
11. Alerts: turn Notification sound off. Expected: the Chime pack row dims. Turn it back on.
12. Backlight: timeout Off. Expected: On battery only and Not in fullscreen games dim. Set it back to your value: they light up, the timeout still fades the keys like in beta.1.
13. Theme > Fluent while on the page, then back to Legend. Expected: the page restyles at once, no broken layout, both themes look clean.
14. Open the page, then hide with F3 or the tray. Expected: it slides away normally; next open shows the main view (compact).
15. Advanced > Reset settings > Cancel. Expected: dialog over the page, Cancel keeps everything.

## C. Chime pack
16. Alerts > Soft, press the play button. Expected: Balanced, then G-Mode on, Soft sounds, no crackle.
17. Soft: F1 (on + off), F2 through Quiet / Balanced / Performance, unplug, plug in. Expected: all 7 Soft sounds play (G-Mode on, G-Mode off, Quiet, Balanced, Performance, Unplug, Plug).
18. Switch to Classic. Expected: it plays the Classic Balanced right away. Repeat 17: all 7 are the 1.2 chimes.
19. Back to Soft. Expected: the next sound is Soft right away (no restart).
20. Notification sound off: F1 / F2 / unplug are silent. Back on.

## D. Toast style
21. Alerts > Pill > Test. Expected: a Balanced pill drops in at the top center (~16 px below the top edge), holds ~1.6 s, slides back up.
22. Pill, Legend: trigger all 8 (F1 on, F1 off, F2 Quiet / Balanced / Performance, F2 on battery = Battery, unplug, plug in). Expected: they match [toasts-legend.png](mockups/1.3-beta2/toasts-legend.png) (round tinted icon, tinted border + soft glow, switch / dots / chip on the right, G-Mode off dim).
23. Pill, Fluent: same 8. Expected: they match [toasts-fluent.png](mockups/1.3-beta2/toasts-fluent.png) (neutral border, blue switch).
24. Classic > Test, then a couple of F2 presses. Expected: the 1.2 card in the top right, same as beta.1. The Notifications subtitle says "Top-right toast…".
25. Back to Pill. Gear > General > Reduce motion ON, F2 a few times. Expected: the pill fades in and out (no slide). Reduce motion OFF.
26. Windows Settings > Accessibility > Visual effects > Animation effects OFF, restart the app, F2. Expected: fade only. Turn it back on.
27. While a pill is up, click where it is. Expected: the click goes to the window behind it (click-through), focus never moves.
28. Borderless game (or a fullscreen browser video): F1 / F2. Expected: the pill shows on top, the game keeps focus, no stutter.
29. F2 to Battery (60 Hz) and back to 240 Hz. Expected: the pill shows after the screen settles and stays ~2.2 s; no lost or double toast.
30. Press F2 quickly 4-5 times. Expected: each new pill replaces the old one at once, never two on screen.

## E. Idle cost
31. Hide the window, wait ~15 s. Expected: Task Manager shows ~0% CPU and a few MB in the tray (first start too: it should drop within ~10 s now).
32. Open the settings page, sit idle 30 s. Expected: ~0% CPU, RAM like the main view (~20-40 MB).
33. After a toast closes (window hidden). Expected: RAM back to a few MB within about a minute, no extra threads or timers in the log.

When everything is ✓ I'll merge into main and publish **v1.3.0**.
