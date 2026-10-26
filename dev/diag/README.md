# Overlay latency diagnostics (branch diag/overlay-latency, not for release)

Opt-in measurement of the overlay's telemetry and render path. Nothing changes unless
`%AppData%\AlienControl\overlay-diag.json` exists with `"enabled": true` (read once at app start, so restart the
app after editing it). Without the file the build behaves like 2.0.2.

```json
{ "enabled": true, "playhead": "ab", "fixedMs": 50, "abSeconds": 20,
  "readerPriority": "BelowNormal", "pipeProbe": true, "etwStatus": true, "raw": true }
```

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
| W_k / B_k | pipe first had bytes (1 ms probe, PeekNamedPipe) / first row of batch k read; a batch ends after 2 ms silence |
| S_j | graph sampled for overlay frame j (FillGraph); e = the newest frame drawn at the right edge |
| Q0_j / Q1_j | Present() called / returned |
| V_j | display QPC of present j from IDXGISwapChain::GetFrameStatistics (SyncQPCTime when PresentCount matches) |

- Age_e2e = V_j - P_e, computed directly.
- D_data = A_e - P_e, D_wait = S_j - A_e (ring + Playhead hold + loop wait), D_present = V_j - S_j.
- Age_e2e = D_data + D_wait + D_present by construction (analyze.py checks it). Playhead's delay is logged as context and
  never added, so arrival delay and hold are not double counted.
- Reader delay = B_k - W_k. Batch gap = B_k - B_(k-1).
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

## Protocol

1. Same game, same display setup (G-SYNC on, 240 Hz panel, same overlay level with the graph, Present mode untouched).
   Castlevania (light) and ratchet-probe or sp26-cod (heavy).
2. `playhead: ab`, 2 minutes per game in a steady scene (two full cycles of each mode).
3. Repeat once with `readerPriority: Normal`.
4. `capture-present-modes.ps1 -Game <exe> -Label overlay-off` with F4 off, then `-Label overlay-on` with the overlay on.
5. Send crash.log and the diag folder.
