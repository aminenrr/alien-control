# Overlay latency diagnostics (branch diag/overlay-latency, not for release)

Opt-in measurement of the overlay's telemetry and render path. Nothing changes unless
`%AppData%\AlienControl\overlay-diag.json` exists with `"enabled": true` (read once at app start, so restart the
app after editing it). Without the file the build behaves like 2.0.2.

Pass 2 (ETW flush A/B, the current protocol):

```json
{ "enabled": true, "playhead": "off", "readerPriority": "BelowNormal", "pump": true,
  "flush": "ab", "flushCycle": [0, 250, 100, 50], "flushSeconds": 30,
  "etwStatus": true, "etwQuery": true, "raw": true }
```

Pass 1 (Playhead A/B): `{ "enabled": true, "playhead": "ab", "fixedMs": 50, "abSeconds": 20, "flush": "0" }`.

- `flush`: `"0"` = no explicit flush (the baseline, exactly pass 1's data path), `"250"` / `"100"` / `"50"` = call
  `ControlTrace(0, "AlienControlsOverlay", props, EVENT_TRACE_CONTROL_FLUSH)` on that period, `"ab"` = cycle `flushCycle`
  every `flushSeconds` (first 2 s after each switch out of the stats). Nothing else changes between periods: same
  PresentMon process and arguments, same reader, same Playhead mode, same renderer. FlushTimer is never touched
  (Windows' minimum is 1 s); every period is explicit flush calls. Each call is logged with its return code, call
  time and actual interval; the flush thread's CPU time is logged per window. The first success and any failing rc
  are written to crash.log, and `ETW control: ... elevated true/false` records the process rights.
- `pump`: a Highest-priority thread reads PresentMon's raw stdout and stamps each chunk as ReadFile returns it (W);
  the reader (at `readerPriority`) parses lines behind it. Replaces pass 1's PeekNamedPipe probe, which could not
  work: on a synchronous pipe handle PeekNamedPipe waits behind the reader's blocking ReadFile, so its stamp went
  stale and reader delay grew without bound.
- `etwStatus`: PresentMon 2.6.0 does not write the Etw* CSV columns for our column set (the unified metrics writer
  in CsvOutput.cpp skips them); it prints `[ETW Status] BufferFillPct=.. EventsLost=.. BuffersLost=..,
  OverflowedPresents=..` on stdout once a second. Those lines are parsed (etw_pm.csv) and no longer counted as rows.
- `etwQuery`: once a second `ControlTrace QUERY` on the same session (etw_query.csv): buffer size and count, free
  buffers, FlushTimer, LogFileMode, EventsLost, RealTimeBuffersLost, LogBuffersLost, BuffersWritten.

- `playhead`: `adaptive` (2.0.2), `fixed` (same clock mapping and glide, constant `fixedMs` hold), `off` (edge = newest
  arrived frame), or `ab` (cycles adaptive, fixed, off every `abSeconds`; the first 2 s after a switch stay out of the
  stats). Same PresentMon session and same renderer in every mode.
- `readerPriority`: the PresentMon reader thread (`BelowNormal` in 2.0.2). Change it in a separate run to test
  reader scheduling.
- With diag on, PresentMon also gets `--qpc_time` (TimeInQPC = the game's Present() call, CPUStartQPC, on the same
  counter as Stopwatch) and `--track_etw_status` (EtwBufferFillPct, EtwBuffersInUse, EtwTotalBuffers, EtwEventsLost,
  EtwBuffersLost, OverflowedPresents).

## Timestamps and equations (all QPC)

| | |
|---|---|
| C_i | CPUStartQPC of game frame i |
| P_i | TimeInQPC: the game's Present() call |
| A_i | its CSV row arrived (ReadLine returned) |
| W_k / B_k | raw stdout chunk holding batch k's first row returned by ReadFile on the pump thread / that row read by the reader; a batch ends after 2 ms silence |
| S_j | graph sampled for overlay frame j (FillGraph); e = the newest frame drawn at the right edge |
| Q0_j / Q1_j | Present() called / returned |
| V_j | display QPC of present j from IDXGISwapChain::GetFrameStatistics (SyncQPCTime when PresentCount matches) |

- Age_e2e = V_j - P_e, computed directly.
- D_data = A_e - P_e, D_wait = S_j - A_e (ring + Playhead hold + loop wait), D_present = V_j - S_j.
- Age_e2e = D_data + D_wait + D_present by construction (analyze.py checks it). Playhead's delay is logged as context and
  never added, so arrival delay and hold are not double counted.
- Reader delay = B_k - W_k (analyze.py checks W_k <= B_k on every batch). Batch gap = B_k - B_(k-1).
- Newest row A-P = for each batch, arrival minus Present() of its newest game frame: how stale the freshest data is
  when it lands. The decisive pass 2 figure, with rows A-P and Age_e2e, per flush period.
- DirectComposition's NextEstimatedFrameTime gives a separate "dcomp est" age, context only, never mixed with V.

## Output

- Every 10 s (and at each mode switch) a `Diag [mode]` block in crash.log: p50 / p95 / max of each quantity, missed
  redraws (same rule as Overlay perf), app and PresentMon CPU, swapchain statistics matched / failed, composition rate,
  ETW status.
- Raw CSVs in `%AppData%\AlienControl\diag\<start time>\` (rows, batches, frames, meta), capped at 200 MB.
- `python analyze.py <run dir> [modes-*.csv]` prints the per mode tables, edge smoothness, and for fixed holds of
  0 to 300 ms the share of frames that would arrive after the edge needs them.

## PresentMon 2.6.0 facts (from its source at tag v2.6.0)

- ETW session (PresentData/PresentMonTraceSession.cpp): real-time mode, BufferSize 64 KB, MinimumBuffers 256,
  MaximumBuffers 1024, FlushTimer not set (0 = ETW default), RAW_TIMESTAMP, QPC clock.
- `--set_circular_buffer_size 8192` sizes PresentMon's own present ring, not the ETW buffers.
- stdout: `fflush` after every CSV row (CsvOutput.cpp), so no CRT block buffering.
- Output thread (OutputThread.cpp): drains completed presents, writes them, then `Sleep(100)`. Rows leave in batches
  at most every ~100 ms whatever ETW does.
- Pass 1 measured ~1 s batches (983 / 1092 ms), not 100 ms, and every row of a batch within 2 to 4 ms.

## Protocol (pass 2)

1. Same setup as pass 1. Control Resonant, FG **off** (frame type isn't tracked in lean mode yet), steady scene.
2. Pass 2 json above (`playhead: off`, `flush: ab`): 4 minutes = two full cycles of 0 / 250 / 100 / 50 ms.
3. Same in Castlevania.
4. Send crash.log and the new diag run folder.

## Protocol (pass 1)

1. Same game, same display setup (G-SYNC on, 240 Hz panel, same overlay level with the graph, Present mode untouched).
   Castlevania (light) and ratchet-probe or sp26-cod (heavy).
2. `playhead: ab`, 2 minutes per game in a steady scene (two full cycles of each mode).
3. Repeat once with `readerPriority: Normal`.
4. `capture-present-modes.ps1 -Game <exe> -Label overlay-off` with F4 off, then `-Label overlay-on` with the overlay on.
5. Send crash.log and the diag folder.

## Pass 3: PresentMon output-drain interval (Amine 2026-10-27)

Question: with the explicit ETW flush at 50 ms, is PresentMon's OutputThread `Sleep(100)` now the limit?
(Pass 2: batch gap floors at ~109 ms = Sleep(100) rounded up to the 15.6 ms tick.)

- `tools\PresentMon\PresentMon-drain.exe` = PresentMon 2.6.0 (tag commit e13fce6) built in CI from source with
  `presentmon-2.6.0-drain.patch` only. The drain interval comes from the section `Local\AlienControlsPmDrain`
  (16 bytes: [0] ms written by Alien Controls, [4] loop counter and [8] magic written by PresentMon).
  100 ms calls the stock `Sleep(100)`; other values wait on a high-resolution waitable timer (Sleep would round up to 15.6 ms).
  Without the section the build behaves exactly like 2.6.0.
- Config: `overlay-diag-pass3.json` (flush fixed 50, `"presentMon": "patched"`, drain cycles 100 / 16 / 10 every 30 s).
  `"presentMon": "stock"` runs the official exe (drain is then always 100).
- Log: every window header carries `drainN`, a `drain:` line with PresentMon's real loop count and interval, and
  PresentMon CPU as % of one core. Raw `batches.csv` / `rows.csv` gain `drain_ms`.

## Step 1: fixed Playhead on stock PresentMon (Amine 2026-10-27)

Official PresentMon 2.6.0 (`"presentMon": "stock"`), explicit flush 50 ms, Playhead modes compared:
- `overlay-diag-step1.json`: `"playhead": "cycle"`, `playCycle` fixed120, off, fixed120, off, adaptive, off (20 s each).
  Tokens: off, adaptive, fixed (uses fixedMs) or fixedNNN. Adaptive is always followed by off.
- `overlay-diag-step1-video-off.json` / `-video-fixed120.json`: one mode for the whole run, for the RTSS screen recordings.
- New per-window log lines: `edge:` (right-edge age at sample, advance per render, render interval),
  `edge stalls:` (renders where the drawn edge did not move, runs of consecutive stalls, jumps >50 ms and >2x the
  render interval, renders where the playhead waited at the newest frame = hold ran dry), `game frametime:` (p50/p95/p99),
  and batch gap with p99.
