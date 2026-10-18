# 1.3.1-beta.2 test checklist (m15 R7)

Only what changed since beta.1. Report by step number: ✓, ✗ + what happened, or "?".
Before you start: Quit beta.1, replace the exe in `C:\Program Files\Alien Controls` (keep the two CLIs), start it.
Log: `%AppData%\AlienControl\crash.log` (send it after part C, and with any ✗).
Mockups: [Lights, Legend](mockups/1.3.1-beta.2/lights-menu-legend.png), [Lights, Fluent](mockups/1.3.1-beta.2/lights-menu-fluent.png), [Notifications](mockups/1.3.1-beta.2/notifications.png).

## A. Lights menu (Legend, then repeat 2-7 in Fluent)
1. Footer shows `v1.3.1-beta.2`. Lighting row: a **laptop icon** where Off was. All lights on: the icon is in the accent color, no dot.
2. Click the laptop. Expected: the menu opens **above** the button, right edges lined up: Keyboard, Rear ring, Alienhead lit (on), divider, **All off** with a lamp-with-a-line icon. Click the laptop again: it closes. Open it, press Esc: closes. Open it, click outside: closes.
3. Tap **Keyboard** once. Expected: keys go dark, the tile goes dark, ring and Alienhead stay on. The laptop gets an amber dot. One tap = one change (no on-then-off flicker).
4. Tap **Keyboard** again. Expected: keys come back in your color **and the 5 white status keys are right** (F1 if G-Mode is on, F12, mute, mic, right Win) without clicking any color.
5. Repeat 3-4 three times quickly, and once with a double-click. Expected: a double-click counts as one tap; status keys right every time it comes back on.
6. Tap **Rear ring**, then **Alienhead**. Expected: each goes off on its own, tiles go dark. With all three off: the 4th tile reads **All on** with a plain lamp, and the laptop icon goes grey (no dot).
7. Tap **All on**: everything comes back, tile reads **All off** again. Tap **All off**: all three go off.
8. Throughout A, the power button light never changes.

## B. Notifications
9. Settings > toast style **Classic**, press F2 a few times and F1. Expected: the card has clearly rounded corners (like the mockup); in Legend the colored bar on the left sits inside the curve.
10. Style **Pill**, F2 / F1 / unplug / plug. Expected: a shorter pill (about two-thirds of the old width), no empty middle; titles, dots, switch and the BATTERY / BALANCED chip all fit, nothing cut off.

## C. F7 test (decides whether the app can follow F7)
11. Lights menu: Keyboard **on**. Settings > Advanced > **F7 test** > Watch F7. During the 20 s countdown: press F7 slowly 4 times (off, mid, full, off...), wait ~2 s between presses. Don't type anything else.
12. Lights menu: Keyboard **off**. Run the F7 test again and press F7 3 times.
13. Send the `F7 probe` lines from crash.log. If any line changes with F7 (a key, a report, or the state bytes), beta.3 makes the Keyboard tile follow F7. If nothing changes, the app can't see F7 and the tile only follows what you tap.

## D. Still fine from beta.1
14. Keyboard + Rear ring off, then: a swatch, an effect, F2, unplug / plug, sleep / wake, Quit + start. Expected: both stay off, the Alienhead follows.
15. Close to the tray, wait a minute: 0% CPU, a few MB.
