"""Alien Controls 1.3.0-beta.2: settings page mockup, Legend + Fluent. python3 settings_mock.py out.png"""
import sys, cairosvg
UI, MONO = "Inter", "DejaVu Sans Mono"
P = {
 "Back": "M5 12h14 M5 12l6 6 M5 12l6 -6",
 "General": "M10.325 4.317c.426 -1.756 2.924 -1.756 3.35 0a1.724 1.724 0 0 0 2.573 1.066c1.543 -.94 3.31 .826 2.37 2.37a1.724 1.724 0 0 0 1.065 2.572c1.756 .426 1.756 2.924 0 3.35a1.724 1.724 0 0 0 -1.066 2.573c.94 1.543 -.826 3.31 -2.37 2.37a1.724 1.724 0 0 0 -2.572 1.065c-.426 1.756 -2.924 1.756 -3.35 0a1.724 1.724 0 0 0 -2.573 -1.066c-1.543 .94 -3.31 -.826 -2.37 -2.37a1.724 1.724 0 0 0 -1.065 -2.572c-1.756 -.426 -1.756 -2.924 0 -3.35a1.724 1.724 0 0 0 1.066 -2.573c-.94 -1.543 .826 -3.31 2.37 -2.37c1 .608 2.296 .07 2.572 -1.065 M9 12a3 3 0 1 0 6 0a3 3 0 0 0 -6 0",
 "Keys": "M2 8a2 2 0 0 1 2 -2h16a2 2 0 0 1 2 2v8a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2l0 -8 M6 10l0 .01 M10 10l0 .01 M14 10l0 .01 M18 10l0 .01 M6 14l0 .01 M18 14l0 .01 M10 14l4 .01",
 "Alerts": "M10 5a2 2 0 1 1 4 0a7 7 0 0 1 4 6v3a4 4 0 0 0 2 3h-16a4 4 0 0 0 2 -3v-3a7 7 0 0 1 4 -6 M9 17v1a3 3 0 0 0 6 0v-1",
 "Backlight": "M3 12h1m8 -9v1m8 8h1m-15.4 -6.4l.7 .7m12.1 -.7l-.7 .7 M9 16a5 5 0 1 1 6 0a3.5 3.5 0 0 0 -1 3a2 2 0 0 1 -4 0a3.5 3.5 0 0 0 -1 -3 M9.7 17l4.6 0",
 "Advanced": "M7 8l-4 4l4 4 M17 8l4 4l-4 4 M14 4l-4 16",
 "Play": "M7 4v16l13 -8z",
}
TH = {
 "legend": dict(BG="#0B0E12", CARD="#0F1318", BORDER="#1E252D", SEG="#11161B", TXT="#E6EDF3", SUB="#8A96A3",
                DIM="#3E4852", ACC="#22D3EE", R=4, legend=True, WIN="#0A0D11"),
 "fluent": dict(BG="#2A2C31", CARD="#2A2C31", BORDER="#34373D", SEG="#222428", TXT="#FFFFFF", SUB="#A6ABB2",
                DIM="#5E6670", ACC="#4CC2FF", R=7, legend=False, WIN="#1E2024"),
}
o = []
def esc(s): return s.replace("&", "&amp;").replace("<", "&lt;")
def t(x, y, s, size=13, col="#fff", w=400, anchor="start", font=UI, ls=0, op=1):
    o.append(f'<text x="{x}" y="{y}" font-family="{font}" font-size="{size}" font-weight="{w}" fill="{col}" fill-opacity="{op}" text-anchor="{anchor}" letter-spacing="{ls}">{esc(s)}</text>')
def icon(k, x, y, s, col, sw=1.8, fill=False, op=1):
    f = col if fill else "none"
    o.append(f'<g opacity="{op}" transform="translate({x},{y}) scale({s/24})" fill="{f}" stroke="{col}" stroke-width="{sw*24/s:.2f}" stroke-linecap="round" stroke-linejoin="round"><path d="{P[k]}"/></g>')
def rect(x, y, w, h, r, fill, stroke="none", fo=1, so=1, op=1):
    o.append(f'<rect opacity="{op}" x="{x}" y="{y}" width="{w}" height="{h}" rx="{r}" fill="{fill}" fill-opacity="{fo}" stroke="{stroke}" stroke-opacity="{so}"/>')

W = 440
ROW_H = 58
TABS = [("General", "General"), ("Keys", "Keys"), ("Alerts", "Alerts"), ("Backlight", "Backlight"), ("Advanced", "Advanced")]

def toggle(x, y, th, on, op=1):
    A = th["ACC"]
    if th["legend"]:
        rect(x, y, 42, 22, 3, A if on else th["SEG"], A if on else "#3A4450", .25 if on else 1, 1, op)
        rect(x + (24 if on else 4), y + 4, 14, 14, 2, A if on else "#4A5560", op=op)
    else:
        rect(x, y, 42, 22, 11, A if on else "none", A if on else "#8A9099", op=op)
        o.append(f'<circle opacity="{op}" cx="{x + (31 if on else 11)}" cy="{y + 11}" r="{6.5 if on else 5.5}" fill="{"#1A1C20" if on else "#C8CDD3"}"/>')

def seg(x, y, w, th, items, sel, op=1, h=30):
    A = th["ACC"]; n = len(items); iw = w / n
    if not th["legend"]: rect(x, y, w, h, th["R"], th["SEG"], th["BORDER"], op=op)
    for i, s in enumerate(items):
        bx = x + i * iw
        if th["legend"]:
            if i == sel: rect(bx + 1, y, iw - 3, h, th["R"], A, A, .12, .8, op)
            else: rect(bx + 1, y, iw - 3, h, th["R"], th["SEG"], th["BORDER"], op=op)
        elif i == sel: rect(bx + 3, y + 3, iw - 6, h - 6, th["R"] - 2, "#3A3D43", op=op)
        if i == sel: rect(bx + iw / 2 - 8, y + h - 4, 16, 2, 1, A, op=op)
        t(bx + iw / 2, y + h / 2 + 4.5, s, 12, th["TXT"] if i == sel else th["SUB"], 600 if i == sel else 400, "middle", op=op)

def button(x, y, w, th, label, op=1, ic=None, h=30):
    rect(x, y, w, h, th["R"], th["SEG"] if th["legend"] else "#35383E", th["BORDER"] if th["legend"] else "#40434A", op=op)
    if ic:
        icon(ic, x + 10, y + h / 2 - 6, 12, th["TXT"], fill=True, sw=1, op=op)
        t(x + 28, y + h / 2 + 4.5, label, 12.5, th["TXT"], 500, op=op)
    else:
        t(x + w / 2, y + h / 2 + 4.5, label, 12.5, th["TXT"], 500, "middle", op=op)

def row(x, y, th, title, sub, ctl=None, op=1, sep=True):
    t(x, y + 22, title, 13.5, th["TXT"], 500, op=op)
    t(x, y + 40, sub, 11.5, th["SUB"], op=op)
    if ctl: ctl(op)
    if sep: o.append(f'<line x1="{x}" y1="{y + ROW_H}" x2="{x + W - 40}" y2="{y + ROW_H}" stroke="{th["BORDER"]}" stroke-opacity=".7"/>')

def page(x, y, th, tab, rows, H):
    leg = th["legend"]; A = th["ACC"]
    rect(x, y, W, H, 8, th["WIN"], "#25303A" if leg else "#3A3D44")
    # header
    icon("Back", x + 18, y + 18, 18, th["TXT"])
    if leg: t(x + 46, y + 32, "SETTINGS", 13, th["TXT"], 700, font=MONO, ls=3)
    else: t(x + 46, y + 32, "Settings", 16, th["TXT"], 600)
    t(x + W - 18, y + 32, "Esc = back", 11, th["DIM"], anchor="end")
    # tabs
    ty = y + 54; tw = (W - 32) / 5
    if not leg: rect(x + 16, ty, W - 32, 52, th["R"] + 1, th["SEG"], th["BORDER"])
    for i, (name, ik) in enumerate(TABS):
        bx = x + 16 + i * tw; on = name == tab
        if leg:
            rect(bx + 2, ty, tw - 4, 52, th["R"], A if on else th["SEG"], A if on else th["BORDER"], .12 if on else 1, .8 if on else 1)
        elif on: rect(bx + 3, ty + 3, tw - 6, 46, th["R"] - 1, "#3A3D43")
        if on: rect(bx + tw / 2 - 9, ty + 47, 18, 2.5, 1.2, A)
        icon(ik, bx + tw / 2 - 8, ty + 9, 16, A if on else th["SUB"])
        lab = name.upper() if leg else name
        t(bx + tw / 2, ty + 41, lab, 9.5 if leg else 11, th["TXT"] if on else th["SUB"], 600 if on else 400, "middle",
          font=MONO if leg else UI, ls=.5 if leg else 0)
    # card with rows
    cy = ty + 66
    rect(x + 12, cy, W - 24, len(rows) * ROW_H + 8, 6 if leg else 8, th["CARD"] if leg else "#2E3035", th["BORDER"])
    ry = cy + 4
    for i, r in enumerate(rows):
        r(x + 24, ry, th, i == len(rows) - 1); ry += ROW_H

R = W - 24  # right edge offset for controls

def T(title, sub, on, op=1):
    return lambda x, y, th, last: row(x, y, th, title, sub, lambda o_: toggle(x + R - 66, y + 18, th, on, o_), op, not last)

def general(dim=False):
    return [
        lambda x, y, th, last: row(x, y, th, "Theme", "Legend or Fluent", lambda o_: seg(x + R - 196, y + 14, 172, th, ["Legend", "Fluent"], 0 if th["legend"] else 1), sep=not last),
        T("Run on startup", "Also in the footer (same setting)", True),
        T("Show in taskbar", "Off: the app only lives in the tray", False),
        T("Reduce motion", "Turns off all animations", False),
        T("Auto preset", "Battery when unplugged, last preset back on AC", True),
    ]
def keys():
    return [
        lambda x, y, th, last: row(x, y, th, "G-key (F1) toggles G-Mode", "Key 0x80", lambda o_: button(x + R - 120, y + 14, 96, th, "Detect key"), sep=not last),
        T("F2 cycles presets", "Quiet, Balanced, Performance", True),
        T("F3 / F4 open the app", "F3 compact, F4 full menu. Again to hide", True),
    ]
def alerts(notif=True, sound=True):
    d1 = 1 if notif else .38; d2 = 1 if sound else .38
    return [
        T("Notifications", "Pill toast for F1, F2 and plug / unplug" if notif else "Off: no toasts", notif),
        lambda x, y, th, last: row(x, y, th, "Toast style", "Top-center pill", lambda o_: (seg(x + R - 222, y + 14, 140, th, ["Pill", "Classic"], 0, d1), button(x + R - 76, y + 14, 52, th, "Test", d1)), d1, not last),
        T("Notification sound", "Plays even with toasts off", sound),
        lambda x, y, th, last: row(x, y, th, "Chime pack", "Soft or the 1.2 sounds", lambda o_: (seg(x + R - 222, y + 14, 140, th, ["Soft", "Classic"], 0, d2), button(x + R - 76, y + 14, 52, th, "", d2, "Play")), d2, not last),
    ]
def backlight(on=True):
    d = 1 if on else .38
    return [
        lambda x, y, th, last: row(x, y, th, "Backlight timeout", "Keys fade after no input", lambda o_: seg(x + R - 230, y + 14, 206, th, ["Off", "1", "2", "5", "10m"], 2 if on else 0), sep=not last),
        T("On battery only", "Stay lit on AC", False, d),
        T("Not in fullscreen games", "Keeps the keys lit while you play", True, d),
    ]
def advanced():
    return [
        T("Log frame gaps", "Each slide's steps/s to crash.log", False),
        lambda x, y, th, last: row(x, y, th, "Reset settings", "Back to defaults, then the app restarts", lambda o_: button(x + R - 90, y + 14, 66, th, "Reset"), sep=not last),
        lambda x, y, th, last: (t(x, y + 22, "Alien Controls 1.3.0-beta.2", 13.5, th["TXT"], 500),
                                t(x, y + 40, "Fans, power and lighting by alienfx-tools (T-Troll, MIT).", 11.5, th["SUB"]),
                                t(x, y + 56, "Not made by Dell or Alienware. Use at your own risk.", 11.5, th["SUB"])),
    ]

PANELS = [
 ("1 · General", "General", general()),
 ("2 · Keys", "Keys", keys()),
 ("3 · Alerts", "Alerts", alerts()),
 ("4 · Alerts, notifications off (style dims)", "Alerts", alerts(notif=False)),
 ("5 · Backlight", "Backlight", backlight()),
 ("6 · Backlight off (rows dim)", "Backlight", backlight(False)),
 ("7 · Advanced", "Advanced", advanced()),
]
PAGE_H = 54 + 66 + 5 * ROW_H + 8 + 16   # tallest tab: no height jump when switching tabs
PAD, GAP = 28, 30
COLS = 2
WW = PAD * 2 + W * 2 + 24
H = 110 + len(PANELS) * (PAGE_H + 26 + GAP)
body_o = []
o.append(f'<rect width="{WW}" height="{H}" fill="#07090C"/>')
t(PAD, 44, "Alien Controls 1.3.0-beta.2 · Settings page (mockup)", 23, "#fff", 700)
t(PAD, 70, "Gear opens a page inside the main window (same card-swap slide as F3/F4), back arrow or Esc returns.", 13, "#8A96A3")
t(PAD, 88, "Five tabs, 5 rows max, fixed height = tallest tab, so nothing scrolls and nothing jumps. Left: Legend · right: Fluent.", 13, "#8A96A3")
y = 116
for label, tab, rows in PANELS:
    t(PAD, y + 14, label, 13.5, "#C9D1D9", 600)
    page(PAD, y + 26, TH["legend"], tab, rows, PAGE_H)
    page(PAD + W + 24, y + 26, TH["fluent"], tab, rows, PAGE_H)
    y += PAGE_H + 26 + GAP
svg = f'<svg xmlns="http://www.w3.org/2000/svg" width="{WW}" height="{y}">' + "".join(o).replace(f'height="{H}" fill="#07090C"', f'height="{y}" fill="#07090C"') + "</svg>"
cairosvg.svg2png(bytestring=svg.encode(), write_to=sys.argv[1], scale=2)
