# 1.3.1-beta.1 test checklist (m15 R7)

Only what's new since 1.3.0. One session, top to bottom. Report by step number: ✓, ✗ + what happened, or "?".
Before you start: Quit 1.3.0, replace the exe in `C:\Program Files\Alien Controls` (keep the two CLIs), start it.
Log: `%AppData%\AlienControl\crash.log` (send it with any ✗, and always after part C).
Mockups: [Legend](mockups/1.3.1/off-menu-legend.png), [Fluent](mockups/1.3.1/off-menu-fluent.png).

## A. Upgrade
1. Start the exe. Expected: footer shows `v1.3.1-beta.1`; theme, presets, lighting color/effect, keyboard timeout and toggles are the same as in 1.3.0. The Off button reads **Off** (or **3 off** if you left 1.3.0 with the lights off: then all three tiles are lit and your color comes back as cyan when you turn them on).
2. Pick **Spectrum** (or Rainbow wave), Quit, start again. Expected: the effect is still Spectrum / Rainbow (1.3.0 fell back to Static after a restart).

## B. Off menu (do it in Legend, then repeat 3-9 in Fluent)
3. Lighting > **Off**. Expected: a small menu drops under the button, right edges lined up, like the mockup: Keyboard, Rear ring, Alienhead, a thin divider, All off; "Turn off / Pick one or more" on top; "F7 still sets keyboard brightness" and "Turn all on" at the bottom. No hitch when it opens.
4. Tap **Keyboard**. Expected: the keys go dark at once, the ring and the Alienhead stay lit. The tile lights up with a check badge. The button now reads **1 off** in the accent color.
5. Tap **Rear ring**. Expected: the back light bar goes off too, the Alienhead stays on. Button: **2 off**.
6. Tap **Keyboard** again. Expected: the keys come back in your color and effect, status keys correct (F1 white if G-Mode is on, mutes, touchpad lock). The ring stays off. Button: **1 off**.
7. Tap **Alienhead** alone (ring still off). Expected: the lid logo goes off. Then **All off**: the keyboard goes off too, all four tiles lit, **3 off**. **All off** again: everything comes back.
8. Turn two off, then **Turn all on**. Expected: everything back on, button reads **Off**.
9. Keyboard: open the menu, press Tab / arrows, Space, then Esc. Expected: focus moves between tiles (visible accent border), Space toggles, Esc closes and focus returns to the Off button. A click outside also closes it.
10. Throughout B, watch the power button. Expected: it never changes (blue on AC, amber on battery).

## C. F7 after Off (the main fix)
11. Keyboard on, press F7 a few times. Expected: brightness steps as usual (baseline, nothing new).
12. Off menu > **Keyboard** off. Press **F7**. Expected: the keyboard comes back, in your color, at the F7 level. Write down: did it light up? Which F7 press (1st, 2nd...)? Were the status keys right?
13. Still after 12, change the color (click a swatch). Expected (known in this beta): the keyboard keeps the color it had before Off until you turn the Keyboard tile back on, since the app can't see F7 yet. Turn the Keyboard tile on: the new color shows.
14. Open crash.log and send the lines with `MacroKeys: unknown Alienware report` (if any appeared when you pressed F7). This tells 1.3.2 what F7 sends, so the tile can follow F7 on its own.

## D. Off survives
With **Keyboard + Rear ring off** (Alienhead on), check each one. Expected every time: keyboard and ring stay off, the Alienhead shows the right color.
15. Click another swatch, then a custom color.
16. Effects: Breathing, Spectrum, Rainbow wave, back to Static. Expected: no keyboard animation runs (Task Manager: ~0% CPU), the Alienhead follows the effect.
17. Brightness slider to 40% and back.
18. F2 through the presets, F1 G-Mode on / off.
19. Unplug, plug back in.
20. Sleep, wake.
21. Quit, start again (and one restart of Windows if you can).
22. Keyboard light timeout set to 1 min, keyboard tile ON, wait 1 min: keys fade as in 1.3.0; move the mouse: they come back. Then keyboard tile OFF and wait 1 min: nothing happens (no fade, keys stay off), moving the mouse doesn't light them.

## E. Idle
23. Close to the tray, wait a minute. Expected: 0% CPU and a few MB, same as 1.3.0.
