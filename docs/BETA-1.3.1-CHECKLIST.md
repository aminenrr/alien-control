# 1.3.1-beta.3 test checklist (m15 R7)

Only what changed since beta.2. Report by step number: ✓, ✗ + what happened, or "?".
Before you start: Quit beta.2, replace the exe in `C:\Program Files\Alien Controls` (keep the two CLIs), start it.
Log: `%AppData%\AlienControl\crash.log` (send it with any ✗).

## A. Keyboard off (Legend, then 1-4 again in Fluent)
1. Footer shows `v1.3.1-beta.3`. Open the Lights menu, tap **Keyboard**. Expected: the **whole keyboard** goes dark (not just the 5 status keys); ring and Alienhead stay on.
2. With Keyboard off: tap **Rear ring** off and on, pick a color swatch, pick an effect, press F2. Expected: the keyboard stays dark every time.
3. Tap **Keyboard** on. Expected: keys back in your color, and the 5 white status keys right (F1 if G-Mode, F12, mute, mic, right Win).
4. Keyboard off, then press F7. Expected: F7 still brings the keys up (the tile stays dark: the app can't see F7).

## B. Turn off / Turn on tile
5. All lights on: the 4th tile reads **Turn off** (lamp with a line) and is in the **accent color**. Tap it: everything goes dark, tile reads **Turn on** (lamp), **plain**.
6. Tap **Turn on**: everything back, accent again.
7. After clicking any tile (including Turn off / on), no colored outline stays on it.

## C. Still fine from beta.2
8. One tap = one change; double-click = one tap. Esc / outside click / laptop button close the menu. Power button light never changes.
9. Classic and Pill notifications look like beta.2.
10. Close to the tray, wait a minute: 0% CPU, a few MB.
