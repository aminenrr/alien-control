# 1.3.1-beta.4 test checklist (m15 R7)

Only what changed since beta.2. Report by step number: ✓, ✗ + what happened, or "?".
Before you start: Quit beta.2 (or beta.3), replace the exe in `C:\Program Files\Alien Controls` (keep the two CLIs), start it.
Log: `%AppData%\AlienControl\crash.log` (send it with any ✗).

## A. Keyboard off (Legend, then 1-4 again in Fluent)
1. Footer shows `v1.3.1-beta.4`. Open the Lights menu, tap **Keyboard**. Expected: the **whole keyboard** goes dark (not just the 5 status keys); ring and Alienhead stay on.
2. With Keyboard off: tap **Rear ring** off and on, pick a color swatch, pick an effect, press F2. Expected: the keyboard stays dark every time.
3. Tap **Keyboard** on. Expected: keys back in your color, and the 5 white status keys right (F1 if G-Mode, F12, mute, mic, right Win).
4. Keyboard off, then press F7. Expected: F7 still brings the keys up (the tile stays dark for now).

## B. Turn off / Turn on tile
5. All lights on: the 4th tile reads **Turn off** (lamp with a line) and is in the **accent color**. Tap it: everything goes dark, tile reads **Turn on** (lamp), **plain**.
6. Tap **Turn on**: everything back, accent again.
7. After clicking any tile (including Turn off / on), no colored outline stays on it.

## C. F7 test (every Alienware collection)
8. Fn-lock in hotkey mode (Esc + Fn on, where F2..F5 run the app's hotkeys; with it off F7 is a plain F7). Settings > Advanced > F7 test > Watch F7. During the 20 s: press **F2 once** (sanity check, it should log), wait 2 s, then F7 slowly 4 times, 2 s apart. Nothing else.
9. Send all `F7 probe` lines from crash.log (the "collections:" line too). If F7 shows up anywhere, beta.5 makes the Keyboard tile follow it.

## D. Still fine from beta.2
10. One tap = one change; double-click = one tap. Esc / outside click / laptop button close the menu. Power button light never changes.
11. Classic and Pill notifications look like beta.2.
12. Close to the tray, wait a minute: 0% CPU, a few MB.
