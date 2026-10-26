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
    with open(path, newline="") as f: return list(csv.DictReader(f))

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

    if rows:
        ap = [ms(int(r["arrival_qpc"]) - int(r["present_qpc"])) for r in rows if int(r["present_qpc"])]
        ac = [(int(r["arrival_qpc"]) - int(r["cpustart_qpc"])) for r in rows if int(r["cpustart_qpc"])]
        print(f"rows: A-P {fmt(ap)}")
        # lateness against the Playhead's own reference (rolling min of A-C over 512 frames): the hold a fixed delay
        # needs so the edge never waits for a frame that hasn't arrived
        from collections import deque
        late, dq = [], deque()
        for i, x in enumerate(ac):
            while dq and ac[dq[-1]] >= x: dq.pop()
            dq.append(i)
            if dq[0] <= i - 512: dq.popleft()
            late.append(ms(x - ac[dq[0]]))
        print(f"lateness vs fastest delivery: {fmt(late)}")
        for h in (0, 16, 33, 50, 75, 100, 150, 200, 300):
            print(f"  fixed hold {h:3d} ms: {100 * sum(1 for x in late if x > h) / len(late):5.1f} % of frames would arrive after the edge needs them")
    if batches:
        st = [int(b["start_qpc"]) for b in batches]
        gaps = [ms(b - a) for a, b in zip(st, st[1:])]
        rd = [ms(int(b["start_qpc"]) - int(b["avail_qpc"])) for b in batches if int(b["avail_qpc"])]
        print(f"\nbatches: gap {fmt(gaps)}")
        print(f"         rows {fmt([int(b['rows']) for b in batches], '')}")
        print(f"         bytes {fmt([int(b['bytes']) for b in batches], '')}")
        print(f"         reader delay W-B {fmt(rd)}  (probe saw no bytes first: {sum(1 for b in batches if not int(b['avail_qpc']))})")

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
