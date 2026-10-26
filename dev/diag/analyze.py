#!/usr/bin/env python3
"""diag (overlay latency experiment, 2026-10-26): reads %AppData%\\AlienControl\\diag\\<run>\\{rows,batches,frames}.csv
and any modes-*.csv from capture-present-modes.ps1, and prints per Playhead mode:

  Age_e2e   = V - P_edge          (display of the overlay frame minus the edge game frame's Present() call), direct
  D_data    = A_edge - P_edge     (row arrival minus Present call)
  D_wait    = S - A_edge          (graph sampled minus arrival: ring + Playhead hold + loop wait)
  D_present = V - S               (overlay frame displayed minus graph sampled)
  Age_e2e == D_data + D_wait + D_present by construction; the script checks it and never adds the Playhead delay.

Also: batch gaps / rows / bytes, reader delay (pipe had bytes -> ReadLine returned), edge smoothness (how far the edge
moves per overlay frame), and the hold needed so a fixed delay rarely runs out of frames.
Pass 2 (explicit ETW flush A/B): rows, batches and frames are split by flush period (flush_ms, 0 = none); per period
it prints A-P of every row and of the newest row in each batch, batch gaps, reader delay B-W (chunk stamp from the
Highest-priority pump -> first row read) with consistency checks against the raw timestamps, every flush attempt
(rc, actual interval, call time) and the ETW counters (PresentMon status lines and our own ControlTrace QUERY).
Usage: python analyze.py <run dir> [modes-*.csv ...]"""
import csv, glob, os, statistics, sys
from collections import defaultdict, Counter

def pct(v, p):
    if not v: return float("nan")
    v = sorted(v); return v[min(len(v) - 1, int(p * (len(v) - 1) + 0.5))]

def fmt(v, unit="ms"):
    return "n/a" if not v else f"p50 {pct(v,.5):7.1f}  p95 {pct(v,.95):7.1f}  max {max(v):7.1f} {unit} (n {len(v)})"

def load(path):
    if not os.path.exists(path): return []
    with open(path, newline="", encoding="utf-8-sig") as f: return list(csv.DictReader(f))   # pass 2: BOM safe

def main(run, extra):
    meta = dict(l.strip().split("=", 1) for l in open(os.path.join(run, "meta.txt")) if "=" in l)
    F = float(meta["qpc_frequency"]); ms = lambda d: d / F * 1000
    frames = load(os.path.join(run, "frames.csv"))
    rows = load(os.path.join(run, "rows.csv"))
    batches = load(os.path.join(run, "batches.csv"))

    # drop the first 2 s after each mode switch (the playhead glides to its new target)
    by = defaultdict(list); last_mode = None; switch = 0
    for r in frames:
        s = int(r["sample_qpc"])
        if r["mode"] != last_mode: last_mode, switch = r["mode"], s
        if s - switch >= 2 * F: by[r["mode"]].append(r)

    print(f"run {run}  playhead={meta.get('playhead')} fixed={meta.get('fixed_ms')} ms  reader={meta.get('reader_priority')}\n")
    for mode, fr in by.items():
        e2e, dd, dw, dp, smp, adv, blk = [], [], [], [], [], [], []
        bad = 0; prev = None
        for r in fr:
            S, P, A, V = int(r["sample_qpc"]), int(r["edge_present_qpc"]), int(r["edge_arrival_qpc"]), int(r["display_qpc"])
            q0, q1 = int(r["present_call_qpc"]), int(r["present_ret_qpc"]); blk.append(ms(q1 - q0))
            if P: smp.append(ms(S - P))
            if prev is not None and P and prev: adv.append(ms(P - prev))
            prev = P
            if V and P:
                a, b, c, t = ms(A - P), ms(S - A), ms(V - S), ms(V - P)
                if abs(a + b + c - t) > 0.01: bad += 1
                e2e.append(t); dd.append(a); dw.append(b); dp.append(c)
        print(f"== {mode}: {len(fr)} overlay frames")
        print(f"  Age_e2e  V-P_edge  {fmt(e2e)}")
        print(f"  D_data   A-P       {fmt(dd)}")
        print(f"  D_wait   S-A       {fmt(dw)}")
        print(f"  D_present V-S      {fmt(dp)}")
        print(f"  age at sample S-P  {fmt(smp)}")
        print(f"  Present block      {fmt(blk)}")
        nz = [x for x in adv if x > 0]
        print(f"  edge advance/draw  {fmt(nz)}; draws with no advance {sum(1 for x in adv if x == 0)}/{len(adv)}")
        if bad: print(f"  WARNING: {bad} frames where the parts don't sum to Age_e2e")
        print()

    def settle(items, tkey):
        # drop the first 2 s after each flush period change
        out = defaultdict(list); last = None; sw = 0
        for r in items:
            t = int(r[tkey]); f = int(r.get("flush_ms", 0) or 0)
            if f != last: last, sw = f, t
            if t - sw >= 2 * F: out[f].append(r)
        return out

    rows_by = settle(rows, "arrival_qpc") if rows else {}
    bat_by = settle(batches, "start_qpc") if batches else {}
    # newest Present() per batch (all batches, then split by the batch's flush period)
    newest = {}
    for r in rows:
        p = int(r["present_qpc"])
        if not p: continue
        k = r["batch"]
        if k not in newest or p > newest[k][0]: newest[k] = (p, int(r["arrival_qpc"]))
    for f in sorted(set(rows_by) | set(bat_by)):
        rs, bs = rows_by.get(f, []), bat_by.get(f, [])
        print(f"\n==== flush {f} ms" + (" (baseline, no explicit flush)" if f == 0 else " (explicit ControlTrace FLUSH)"))
        ap = [ms(int(r["arrival_qpc"]) - int(r["present_qpc"])) for r in rs if int(r["present_qpc"])]
        print(f"  rows A-P            {fmt(ap)}")
        nb = [ms(newest[b["batch"]][1] - newest[b["batch"]][0]) for b in bs if b["batch"] in newest]
        print(f"  newest row A-P      {fmt(nb)}")
        st = [int(b["start_qpc"]) for b in bs]
        gaps = [ms(y - x) for x, y in zip(st, st[1:]) if y - x < 5 * F]
        print(f"  batch gap           {fmt(gaps)}")
        print(f"  batch rows          {fmt([int(b['rows']) for b in bs], '')}")
        ck = "chunk_qpc" if bs and "chunk_qpc" in bs[0] else "avail_qpc"
        rd = [ms(int(b["start_qpc"]) - int(b[ck])) for b in bs if int(b[ck])]
        print(f"  reader delay B-W    {fmt(rd)}  (batches without a stamp: {sum(1 for b in bs if not int(b[ck]))})")
        if ck == "chunk_qpc" and bs:
            # validation against the raw timestamps: W never after B, and a stamp never reused by two batches unless the
            # reader was behind (then B-W grows, which is what it measures)
            neg = sum(1 for b in bs if int(b[ck]) and int(b[ck]) > int(b["start_qpc"]))
            seqs = Counter(b["chunk_seq"] for b in bs if int(b[ck]))
            reused = sum(v - 1 for v in seqs.values() if v > 1)
            print(f"  check: W after B {neg}, chunk shared by 2+ batches {reused}" + ("  OK" if neg == 0 else "  FAIL"))

    flushes = load(os.path.join(run, "flushes.csv"))
    if flushes:
        print("\nflush attempts")
        per = defaultdict(list)
        for r in flushes: per[int(r["period_ms"])].append(r)
        for f, rs in sorted(per.items()):
            rc = Counter(r["rc"] for r in rs)
            iv = [float(r["interval_ms"]) for r in rs if r["interval_ms"] not in ("NaN", "", "nan")]
            print(f"  period {f:4d} ms: {len(rs)} calls, rc {dict(rc)}; interval {fmt(iv)}; call {fmt([float(r['call_ms']) for r in rs])}")
    for name in ("etw_pm", "etw_query"):
        e = load(os.path.join(run, name + ".csv"))
        if not e: continue
        print(f"\n{name} ({'PresentMon [ETW Status] lines' if name == 'etw_pm' else 'our ControlTrace QUERY'})")
        keys = ["events_lost", "buffers_lost", "overflowed_presents"] if name == "etw_pm" else ["events_lost", "realtime_buffers_lost", "log_buffers_lost"]
        if name == "etw_query":
            bad = Counter(r["rc"] for r in e if r["rc"] != "0")
            e = [r for r in e if r["rc"] == "0"]
            if bad: print(f"  failed queries: {dict(bad)}")
            if e: print(f"  session: {e[-1]['buffer_kb']} KB buffers, {e[-1]['number_of_buffers']} in use of max {e[-1]['max_buffers']}, FlushTimer {e[-1]['flush_timer_s']} s, mode {e[-1]['log_file_mode']}")
        per = defaultdict(list)
        for r in e: per[int(r.get("flush_ms", 0) or 0)].append(r)
        for f, rs in sorted(per.items()):
            d = ", ".join(f"{k} +{int(rs[-1][k]) - int(rs[0][k])} (total {rs[-1][k]})" for k in keys)
            print(f"  flush {f:4d} ms, {len(rs)} samples: {d}")

    for path in extra:
        m = load(path)
        if not m: continue
        print(f"\n{os.path.basename(path)}")
        per = defaultdict(Counter); disp = defaultdict(list); shown = Counter(); tot = Counter()
        for r in m:
            app = r.get("Application", "?")
            per[app][r.get("PresentMode", "?")] += 1
            tot[app] += 1
            u = r.get("MsUntilDisplayed", "NA")
            if u not in ("NA", "", None):
                try: disp[app].append(float(u)); shown[app] += 1
                except ValueError: pass
        for app, c in per.items():
            modes = ", ".join(f"{k} {100 * v / tot[app]:.0f} %" for k, v in c.most_common())
            print(f"  {app}: {tot[app]} presents, {modes}; displayed {shown[app]}; MsUntilDisplayed {fmt(disp[app])}")

if __name__ == "__main__":
    if len(sys.argv) < 2: print(__doc__); sys.exit(1)
    main(sys.argv[1], sys.argv[2:] or glob.glob(os.path.join(os.path.dirname(os.path.abspath(sys.argv[1])), "modes-*.csv")))
