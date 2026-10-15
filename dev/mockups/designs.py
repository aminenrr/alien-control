"""Three redesign directions for Alien Controls, 420 DIP card (Window Width=440 incl. 10 DIP shadow margin)."""
import sys
from lib import *

F = lambda n, s=20, filled=False: icon("fluent", n, s, filled)
FAN = lambda s=18: icon("phosphor", "fan", s)          # Fluent has no fan glyph -> one MIT Path from Phosphor

PERF = [("battery_saver", "Battery"), ("weather_moon", "Quiet"), ("scales", "Balanced"), ("gauge", "Performance"), ("fire", "G-Mode")]
GPU = [("leaf_two", "Eco"), ("laptop", "Standard"), ("flash", "Ultimate"), ("flash_auto", "Optimized")]
PERF_ON, GPU_ON = "Balanced", "Standard"
STATS = [("CPU", "62", "°C", 62), ("CPU FAN", "41", "%", 41), ("GPU", "58", "°C", 58), ("GPU FAN", "38", "%", 38)]
SWATCH = ["#00E5FF", "#7C4DFF", "#FF4D6A", "#3CFF7A", "#FFFFFF"]
RAINBOW = "conic-gradient(from 0deg,#f00,#ff0,#0f0,#0ff,#00f,#f0f,#f00)"
CHEV = '<svg width="12" height="12" viewBox="0 0 12 12" style="flex:none"><path d="M2.5 4.5 6 8l3.5-3.5" fill="none" stroke="currentColor" stroke-width="1.3"/></svg>'


def page(css, body, bg="#0b0d11"):
    return (f'<!doctype html><html><head><meta charset="utf-8"><style>{FONT_CSS}'
            f'*{{box-sizing:border-box;margin:0;padding:0}} body{{background:{bg};width:440px;padding:10px}} {css}</style></head>'
            f'<body>{body}</body></html>')


# ───────────────────────── A · Refined Nebula ─────────────────────────
CSS_A = """
body{font-family:SegoeSub,sans-serif;color:#E6EBF1}
.win{width:420px;border-radius:14px;border:1px solid rgba(255,255,255,.09);overflow:hidden;
 background:radial-gradient(120% 60% at 0% 0%,rgba(0,229,255,.07),transparent 60%),radial-gradient(100% 50% at 100% 100%,rgba(124,77,255,.07),transparent 60%),#15181D;
 box-shadow:0 8px 28px rgba(0,0,0,.6)}
.hdr{display:flex;align-items:center;justify-content:space-between;padding:10px 8px 10px 16px}
.brand{display:flex;align-items:center;gap:10px}.brand img{height:20px}
.brand b{font-size:15px;font-weight:600;letter-spacing:.2px}
.chip{font-size:11px;color:#9AA6B4;border:1px solid rgba(255,255,255,.1);border-radius:999px;padding:1px 8px}
.hb{display:flex;gap:2px}.hb div{width:32px;height:28px;border-radius:7px;display:flex;align-items:center;justify-content:center;color:#A7B1BD}
.hair{height:1px;background:linear-gradient(90deg,transparent,rgba(0,229,255,.55),rgba(124,77,255,.55),transparent)}
.body{padding:12px 14px 8px}
.stats{display:grid;grid-template-columns:repeat(4,1fr);gap:6px}
.st{background:#1C2027;border:1px solid #252A32;border-radius:10px;padding:9px 10px 10px}
.st .k{font-size:9.5px;letter-spacing:.9px;color:#8793A1;white-space:nowrap;display:flex;justify-content:space-between;align-items:center}
.st .v{margin-top:3px;white-space:nowrap;font-variant-numeric:tabular-nums}.st .v b{font-size:20px;font-weight:600;color:#F4F7FA}.st .v i{font-style:normal;font-size:11px;color:#8793A1;margin-left:2px}
.bar{height:3px;border-radius:2px;background:rgba(255,255,255,.07);margin-top:7px;overflow:hidden}.bar>div{height:100%;border-radius:2px;background:linear-gradient(90deg,#00E5FF,#7C4DFF)}
.w2{grid-column:span 2}
.clean{display:flex;align-items:center;gap:4px;color:#5FE3F5;letter-spacing:.2px;font-size:10px}
.pwr{display:flex;align-items:center;justify-content:center;gap:6px;margin:9px 0 2px;font-size:11.5px;color:#8793A1;white-space:nowrap}
.pwr b{color:#DDE4EC;font-weight:600}.pwr .dot{color:#4A5361}.pwr svg{color:#FFC861}
.sec{margin-top:14px}
.sh{display:flex;justify-content:space-between;align-items:center;margin:0 2px 7px}
.sh b{display:flex;align-items:center;gap:7px;font-size:13px;font-weight:600;color:#EEF2F6;white-space:nowrap}.sh b svg{color:#4FDDF2}
.sh span{font-size:11.5px;color:#8793A1;white-space:nowrap}
.grid{display:grid;gap:6px}.c5{grid-template-columns:repeat(5,1fr)}.c4{grid-template-columns:repeat(4,1fr)}.c3{grid-template-columns:repeat(3,1fr)}
.mb{background:#1C2027;border:1px solid #252A32;border-radius:10px;padding:10px 2px 8px;text-align:center;font-size:11.5px;color:#BAC4CF;white-space:nowrap}
.mb .ic{display:flex;justify-content:center;margin-bottom:5px;color:#9AA6B4}
.on{border-color:rgba(0,216,240,.85)!important;background:linear-gradient(180deg,rgba(0,229,255,.17),rgba(124,77,255,.06))!important;color:#fff!important;box-shadow:0 0 14px rgba(0,229,255,.12)}
.on .ic{color:#3FE6FF!important}
.red.on{border-color:#FF4D6A!important;background:linear-gradient(180deg,rgba(255,77,106,.2),rgba(255,77,106,.05))!important}.red.on .ic{color:#FF5C76!important}
.sb{background:#1C2027;border:1px solid #252A32;border-radius:9px;padding:8px 2px;text-align:center;font-size:12px;color:#BAC4CF;white-space:nowrap}
.dis{opacity:.4}
.row{display:flex;align-items:center}
.gk{margin:7px 2px 0;font-size:11.5px;color:#8793A1}
.tg{width:36px;height:18px;border-radius:9px;background:linear-gradient(90deg,#00D8F0,#7C4DFF);position:relative;flex:none}.tg:after{content:"";position:absolute;right:3px;top:3px;width:12px;height:12px;border-radius:6px;background:#0D1014}
.ghost{border:1px solid #2A303A;background:#1C2027;border-radius:8px;padding:5px 10px;font-size:11.5px;color:#BAC4CF;white-space:nowrap;display:flex;align-items:center;gap:6px}
.light{background:#1C2027;border:1px solid #252A32;border-radius:10px;padding:10px}
.sw{width:22px;height:22px;border-radius:7px;border:1px solid rgba(255,255,255,.18);flex:none}
.sw.sel{box-shadow:0 0 0 2px #15181D,0 0 0 3.5px #00E5FF}
.combo{flex:1;height:30px;border-radius:8px;background:#15181D;border:1px solid #2A303A;display:flex;align-items:center;justify-content:space-between;padding:0 10px;font-size:12px;color:#E6EBF1;white-space:nowrap}
.combo em{font-style:normal;color:#8793A1;margin-right:6px}
.slider{position:relative;height:4px;border-radius:2px;background:rgba(255,255,255,.1);flex:1}
.slider .f{position:absolute;left:0;top:0;bottom:0;width:80%;border-radius:2px;background:linear-gradient(90deg,#00E5FF,#7C4DFF)}
.slider .t{position:absolute;left:80%;top:50%;width:14px;height:14px;margin:-7px 0 0 -7px;border-radius:7px;background:#fff;box-shadow:0 0 0 3px rgba(124,77,255,.35)}
.ftr{display:flex;align-items:center;justify-content:space-between;padding:9px 14px;border-top:1px solid rgba(255,255,255,.06);margin-top:12px;position:relative}
.ftr .ver{position:absolute;left:50%;transform:translateX(-50%);font-size:11px;color:#6F7A88}
"""

def body_A():
    st = "".join(f'<div class="st"><div class="k">{k}</div><div class="v"><b>{v}</b><i>{u}</i></div><div class="bar"><div style="width:{p}%"></div></div></div>' for k, v, u, p in STATS)
    st += (f'<div class="st w2"><div class="k">RAM<span class="clean">{F("broom",12)}click to clean</span></div><div class="v"><b>13.5</b><i>/ 32 GB</i></div><div class="bar"><div style="width:42%"></div></div></div>'
           f'<div class="st w2"><div class="k">VRAM</div><div class="v"><b>1.7</b><i>/ 8 GB</i></div><div class="bar"><div style="width:21%"></div></div></div>')
    perf = "".join(f'<div class="mb{" on" if l==PERF_ON else ""}{" red" if l=="G-Mode" else ""}"><div class="ic">{F(n,20)}</div>{l}</div>' for n, l in PERF)
    gpu = "".join(f'<div class="mb{" on" if l==GPU_ON else ""}"><div class="ic">{F(n,20)}</div>{l}</div>' for n, l in GPU)
    cpu = "".join(f'<div class="sb{" on" if l=="Aggressive" else ""}">{l}</div>' for l in ["Off", "On", "Efficient", "Aggressive"])
    fan = '<div class="sb on">Auto</div><div class="sb">Max Boost</div><div class="sb dis">Custom curve…</div>'
    sw = "".join(f'<div class="sw{" sel" if i==0 else ""}" style="background:{c}"></div>' for i, c in enumerate(SWATCH)) + f'<div class="sw" style="background:{RAINBOW}"></div>'
    sec = lambda ic, t, h, inner: f'<div class="sec"><div class="sh"><b>{ic}{t}</b><span>{h}</span></div>{inner}</div>'
    return f"""<div class="win">
<div class="hdr"><div class="brand"><img src="{LOGO}"><b>Alien Controls</b><span class="chip">m15 R7</span></div><div class="hb"><div>{F('subtract',16)}</div><div>{F('dismiss',16)}</div></div></div>
<div class="hair"></div>
<div class="body">
<div class="stats">{st}</div>
<div class="pwr">{F('flash',13)}<span>CPU <b>38 W</b></span><span class="dot">·</span><span>GPU <b>72 W</b></span><span class="dot">·</span><span>Total <b>118 W</b></span></div>
{sec(F('pulse',16),'Performance Mode','Balanced',f'<div class="grid c5">{perf}</div><div class="row gk"><span style="flex:1">G-key (F1) toggles G-Mode</span><div class="tg" style="margin-right:8px"></div><div class="ghost">Detect key</div></div>')}
{sec(F('games',16),'GPU Mode','Standard · RTX 3070 Ti',f'<div class="grid c4">{gpu}</div>')}
{sec(F('developer_board',16),'CPU Boost','Plugged in',f'<div class="grid c4">{cpu}</div>')}
{sec(FAN(16),'Fans','BIOS controlled',f'<div class="grid c3">{fan}</div>')}
{sec(F('lightbulb_filament',16),'Lighting','Brightness 80%',f'<div class="light"><div class="row" style="gap:7px">{sw}<div style="flex:1"></div><div class="ghost">{F("power",13)}Off</div></div><div class="row" style="gap:12px;margin-top:10px"><div class="combo"><span><em>Effect</em>Static</span>{CHEV}</div><div class="row" style="flex:1.25;gap:8px">{F("weather_sunny",14) if False else ""}<div class="slider"><div class="f"></div><div class="t"></div></div><span style="font-size:11px;color:#8793A1;width:26px;text-align:right">80%</span></div></div></div>')}
</div>
<div class="ftr"><div class="row" style="gap:8px"><div class="tg"></div><span style="font-size:12px;color:#BAC4CF">Run on startup</span></div><div class="ver">v0.7.0</div><div class="ghost">{F('power',13)}Quit</div></div>
</div>"""


# ───────────────────────── B · AWCC Terminal ─────────────────────────
CSS_B = """
body{font-family:SegoeSub,sans-serif;color:#D9E1E8}
.mono{font-family:CascMono,Consolas,monospace}
.win{width:420px;border-radius:10px;border:1px solid #1D2228;overflow:hidden;background:#090A0C;box-shadow:0 8px 28px rgba(0,0,0,.7)}
.top{height:2px;background:linear-gradient(90deg,rgba(0,229,255,0),#00E5FF 18%,#00E5FF 50%,#7C4DFF 82%,rgba(124,77,255,0))}
.hdr{display:flex;align-items:center;justify-content:space-between;padding:10px 8px 10px 14px;border-bottom:1px solid #16191E;
 background:linear-gradient(180deg,rgba(0,229,255,.06),rgba(0,0,0,0)),url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='28' height='24'><path d='M7 0h14l7 12-7 12H7L0 12z' fill='none' stroke='%2300E5FF' stroke-opacity='.06'/></svg>")}
.brand{display:flex;align-items:center;gap:10px}.brand img{height:20px}
.brand b{font-size:12.5px;font-weight:700;letter-spacing:2.4px}
.brand span{font-size:10.5px;color:#00D8F0;letter-spacing:1px;padding:1px 6px;border:1px solid rgba(0,216,240,.35);border-radius:3px}
.hb{display:flex}.hb div{width:32px;height:26px;display:flex;align-items:center;justify-content:center;color:#7F8B97}
.body{padding:12px 12px 6px}
.stats{display:grid;grid-template-columns:repeat(4,1fr);gap:1px;background:#1A1E24;border:1px solid #1A1E24;border-radius:6px;overflow:hidden}
.st{background:#0E1013;padding:8px 9px 9px}
.st .k{font-size:9px;letter-spacing:1.2px;color:#6E7A86;white-space:nowrap;display:flex;justify-content:space-between}
.st .v{margin-top:4px;white-space:nowrap}.st .v b{font-size:19px;font-weight:600;color:#F2F6F9}.st .v i{font-style:normal;font-size:10.5px;color:#6E7A86;margin-left:3px}
.ticks{height:4px;margin-top:7px;background:repeating-linear-gradient(90deg,#1C2127 0 6px,transparent 6px 8px)}
.ticks>div{height:100%;background:repeating-linear-gradient(90deg,#00E5FF 0 6px,transparent 6px 8px)}
.w2{grid-column:span 2}
.clean{color:#00D8F0;display:flex;gap:4px;align-items:center}
.pwr{display:flex;justify-content:space-between;align-items:center;margin-top:8px;padding:6px 9px;border:1px solid #1A1E24;border-radius:6px;background:#0C0E10;font-size:11px;color:#6E7A86;white-space:nowrap}
.pwr b{color:#E6EDF3;font-weight:600}.pwr .tot b{color:#00E5FF}
.sec{margin-top:14px}
.sh{display:flex;align-items:center;gap:8px;margin-bottom:7px;white-space:nowrap}
.sh .n{font-size:10px;color:#00D8F0}
.sh b{font-size:11px;letter-spacing:1.8px;font-weight:700;color:#E9EEF2;display:flex;align-items:center;gap:7px}.sh b svg{color:#00D8F0}
.sh .ln{flex:1;height:1px;background:linear-gradient(90deg,#20262D,rgba(32,38,45,0))}
.sh span{font-size:10px;color:#6E7A86;letter-spacing:.3px}
.grid{display:grid;gap:5px}.c5{grid-template-columns:repeat(5,1fr)}.c4{grid-template-columns:repeat(4,1fr)}.c3{grid-template-columns:repeat(3,1fr)}
.mb{position:relative;background:#0E1013;border:1px solid #1C2127;border-radius:4px;padding:10px 2px 9px;text-align:center;font-size:11px;color:#A9B4BF;white-space:nowrap}
.mb .ic{display:flex;justify-content:center;margin-bottom:6px;color:#7F8B97}
.sb{position:relative;background:#0E1013;border:1px solid #1C2127;border-radius:4px;padding:8px 2px;text-align:center;font-size:11.5px;color:#A9B4BF;white-space:nowrap}
.on{border-color:rgba(0,216,240,.5)!important;background:linear-gradient(180deg,rgba(0,229,255,.09),rgba(0,229,255,.02))!important;color:#fff!important}
.on:after{content:"";position:absolute;left:22%;right:22%;bottom:-1px;height:2px;background:#00E5FF;box-shadow:0 0 8px #00E5FF}
.on .ic{color:#00E5FF!important}
.red.on{border-color:rgba(255,59,78,.55)!important;background:linear-gradient(180deg,rgba(255,59,78,.12),rgba(255,59,78,.02))!important}.red.on:after{background:#FF3B4E;box-shadow:0 0 8px #FF3B4E}.red.on .ic{color:#FF4D5E!important}
.dis{opacity:.35}
.row{display:flex;align-items:center}
.gk{margin:7px 1px 0;font-size:11px;color:#6E7A86}
.tg{width:34px;height:16px;border-radius:3px;background:rgba(0,229,255,.18);border:1px solid #00D8F0;position:relative;flex:none}.tg:after{content:"";position:absolute;right:2px;top:2px;width:10px;height:10px;border-radius:1px;background:#00E5FF}
.ghost{border:1px solid #232930;background:#0E1013;border-radius:4px;padding:5px 9px;font-size:11px;color:#A9B4BF;white-space:nowrap;display:flex;align-items:center;gap:6px}
.light{background:#0E1013;border:1px solid #1C2127;border-radius:6px;padding:9px}
.sw{width:20px;height:20px;border-radius:3px;border:1px solid rgba(255,255,255,.15);flex:none}
.sw.sel{outline:1px solid #00E5FF;outline-offset:2px}
.combo{flex:1;height:28px;border-radius:4px;background:#090A0C;border:1px solid #232930;display:flex;align-items:center;justify-content:space-between;padding:0 9px;font-size:11.5px;color:#E6EDF3;white-space:nowrap}
.combo em{font-style:normal;color:#6E7A86;margin-right:6px;font-size:10px;letter-spacing:1px}
.slider{position:relative;height:2px;background:#232930;flex:1}
.slider .f{position:absolute;left:0;top:0;bottom:0;width:80%;background:#00E5FF;box-shadow:0 0 6px rgba(0,229,255,.6)}
.slider .t{position:absolute;left:80%;top:50%;width:10px;height:14px;margin:-7px 0 0 -5px;background:#E6EDF3;border-radius:2px}
.ftr{display:flex;align-items:center;justify-content:space-between;padding:8px 12px;border-top:1px solid #16191E;margin-top:12px;position:relative;background:#07080A}
.ftr .ver{position:absolute;left:50%;transform:translateX(-50%);font-size:10.5px;color:#56616C}
"""

def body_B():
    st = "".join(f'<div class="st"><div class="k mono">{k}</div><div class="v mono"><b>{v}</b><i>{u}</i></div><div class="ticks"><div style="width:{p}%"></div></div></div>' for k, v, u, p in STATS)
    st += (f'<div class="st w2"><div class="k mono">RAM<span class="clean">{F("broom",11)}CLICK TO CLEAN</span></div><div class="v mono"><b>13.5</b><i>/ 32 GB</i></div><div class="ticks"><div style="width:42%"></div></div></div>'
           f'<div class="st w2"><div class="k mono">VRAM</div><div class="v mono"><b>1.7</b><i>/ 8 GB</i></div><div class="ticks"><div style="width:21%"></div></div></div>')
    perf = "".join(f'<div class="mb{" on" if l==PERF_ON else ""}{" red" if l=="G-Mode" else ""}"><div class="ic">{F(n,20)}</div>{l}</div>' for n, l in PERF)
    gpu = "".join(f'<div class="mb{" on" if l==GPU_ON else ""}"><div class="ic">{F(n,20)}</div>{l}</div>' for n, l in GPU)
    cpu = "".join(f'<div class="sb{" on" if l=="Aggressive" else ""}">{l}</div>' for l in ["Off", "On", "Efficient", "Aggressive"])
    fan = '<div class="sb on">Auto</div><div class="sb">Max Boost</div><div class="sb dis">Custom curve…</div>'
    sw = "".join(f'<div class="sw{" sel" if i==0 else ""}" style="background:{c}"></div>' for i, c in enumerate(SWATCH)) + f'<div class="sw" style="background:{RAINBOW}"></div>'
    sec = lambda i, ic, t, h, inner: f'<div class="sec"><div class="sh"><span class="n mono">{i:02d}</span><b>{ic}{t.upper()}</b><div class="ln"></div><span class="mono">{h}</span></div>{inner}</div>'
    return f"""<div class="win"><div class="top"></div>
<div class="hdr"><div class="brand"><img src="{LOGO}"><b>ALIEN CONTROLS</b><span class="mono">m15 R7</span></div><div class="hb"><div>{F('subtract',15)}</div><div>{F('dismiss',15)}</div></div></div>
<div class="body">
<div class="stats">{st}</div>
<div class="pwr mono"><span>PWR</span><span>CPU <b>38 W</b></span><span>·</span><span>GPU <b>72 W</b></span><span>·</span><span class="tot">Total <b>118 W</b></span></div>
{sec(1,F('pulse',14),'Performance Mode','BALANCED',f'<div class="grid c5">{perf}</div><div class="row gk"><span style="flex:1">G-key (F1) toggles G-Mode</span><div class="tg" style="margin-right:8px"></div><div class="ghost">Detect key</div></div>')}
{sec(2,F('games',14),'GPU Mode','STANDARD · 3070 Ti',f'<div class="grid c4">{gpu}</div>')}
{sec(3,F('developer_board',14),'CPU Boost','AC',f'<div class="grid c4">{cpu}</div>')}
{sec(4,FAN(14),'Fans','BIOS',f'<div class="grid c3">{fan}</div>')}
{sec(5,F('lightbulb_filament',14),'Lighting','80%',f'<div class="light"><div class="row" style="gap:7px">{sw}<div style="flex:1"></div><div class="ghost">{F("power",12)}Off</div></div><div class="row" style="gap:12px;margin-top:10px"><div class="combo"><span><em class="mono">FX</em>Static</span>{CHEV}</div><div class="row" style="flex:1.25;gap:8px"><div class="slider"><div class="f"></div><div class="t"></div></div><span class="mono" style="font-size:10.5px;color:#6E7A86;width:28px;text-align:right">80%</span></div></div></div>')}
</div>
<div class="ftr"><div class="row" style="gap:8px"><div class="tg"></div><span style="font-size:11.5px;color:#A9B4BF">Run on startup</span></div><div class="ver mono">v0.7.0</div><div class="ghost">{F('power',12)}Quit</div></div>
</div>"""


# ───────────────────────── C · Fluent Mica ─────────────────────────
CSS_C = """
body{font-family:SegoeSub,sans-serif;color:#FFFFFF}
.win{width:420px;border-radius:8px;border:1px solid rgba(255,255,255,.08);overflow:hidden;
 background:linear-gradient(155deg,#1E2633 0%,#202024 42%,#221E29 100%);box-shadow:0 8px 32px rgba(0,0,0,.55)}
.hdr{display:flex;align-items:center;justify-content:space-between;height:40px;padding-left:14px}
.brand{display:flex;align-items:center;gap:10px}.brand img{height:16px}
.brand b{font-size:12.5px;font-weight:400}.brand span{font-size:12px;color:rgba(255,255,255,.6)}
.hb{display:flex;align-self:stretch}.hb div{width:44px;display:flex;align-items:center;justify-content:center;color:rgba(255,255,255,.8)}
.body{padding:2px 12px 10px}
.card{background:rgba(255,255,255,.045);border:1px solid rgba(255,255,255,.065);border-radius:8px;padding:12px;margin-top:6px}
.stats{display:grid;grid-template-columns:repeat(4,1fr)}
.st{padding:0 8px;border-left:1px solid rgba(255,255,255,.07)}.st:first-child{border-left:0;padding-left:2px}
.st .k{font-size:11px;color:rgba(255,255,255,.62);white-space:nowrap;display:flex;justify-content:space-between;align-items:center}
.st .v{margin-top:1px;white-space:nowrap}.st .v b{font-size:20px;font-weight:600}.st .v i{font-style:normal;font-size:11px;color:rgba(255,255,255,.62);margin-left:2px}
.bar{height:4px;border-radius:2px;background:rgba(255,255,255,.1);margin-top:6px;overflow:hidden}.bar>div{height:100%;border-radius:2px;background:#4CC2FF}
.mem{display:grid;grid-template-columns:1fr 1fr;margin-top:12px;padding-top:11px;border-top:1px solid rgba(255,255,255,.07)}
.mem .st:first-child{padding-left:2px}
.clean{display:flex;align-items:center;gap:4px;color:#4CC2FF;font-size:11px}
.pwr{display:flex;align-items:center;gap:6px;margin-top:11px;padding-top:9px;border-top:1px solid rgba(255,255,255,.07);font-size:11.5px;color:rgba(255,255,255,.62);white-space:nowrap}
.pwr b{color:#fff;font-weight:600}.pwr svg{color:#4CC2FF}
.sh{display:flex;justify-content:space-between;align-items:center;margin-bottom:10px}
.sh b{display:flex;align-items:center;gap:10px;font-size:13.5px;font-weight:600;white-space:nowrap}.sh b svg{color:rgba(255,255,255,.85)}
.sh span{font-size:12px;color:rgba(255,255,255,.62);white-space:nowrap}
.track{display:grid;background:rgba(0,0,0,.22);border:1px solid rgba(255,255,255,.05);border-radius:7px;padding:3px;gap:2px}
.c5{grid-template-columns:repeat(5,1fr)}.c4{grid-template-columns:repeat(4,1fr)}.c3{grid-template-columns:repeat(3,1fr)}
.seg{position:relative;border-radius:5px;text-align:center;font-size:11.5px;color:rgba(255,255,255,.78);white-space:nowrap;padding:7px 1px}
.seg.tall{padding:9px 1px 10px}
.seg .ic{display:flex;justify-content:center;margin-bottom:4px;color:rgba(255,255,255,.72)}
.seg.on{background:rgba(255,255,255,.085);color:#fff;box-shadow:0 1px 0 rgba(0,0,0,.25),inset 0 1px 0 rgba(255,255,255,.05)}
.seg.on:after{content:"";position:absolute;left:50%;bottom:2px;width:16px;margin-left:-8px;height:3px;border-radius:2px;background:#4CC2FF}
.seg.on .ic{color:#4CC2FF}
.seg.red.on:after{background:#FF6B7F}.seg.red.on .ic{color:#FF6B7F}
.dis{opacity:.4}
.row{display:flex;align-items:center}
.gk{margin-top:10px;font-size:12px;color:rgba(255,255,255,.62)}
.tg{width:40px;height:20px;border-radius:10px;background:#4CC2FF;position:relative;flex:none}.tg:after{content:"";position:absolute;right:4px;top:4px;width:12px;height:12px;border-radius:6px;background:#000}
.btn{background:rgba(255,255,255,.06);border:1px solid rgba(255,255,255,.07);border-bottom-color:rgba(255,255,255,.03);border-radius:5px;padding:5px 11px;font-size:12px;color:#fff;white-space:nowrap;display:flex;align-items:center;gap:6px}
.sw{width:22px;height:22px;border-radius:11px;flex:none;border:1px solid rgba(255,255,255,.15)}
.sw.sel{box-shadow:0 0 0 2px #262629,0 0 0 4px #fff}
.combo{flex:1;height:32px;border-radius:5px;background:rgba(255,255,255,.06);border:1px solid rgba(255,255,255,.07);border-bottom-color:rgba(255,255,255,.22);display:flex;align-items:center;justify-content:space-between;padding:0 11px;font-size:12.5px;white-space:nowrap}
.combo em{font-style:normal;color:rgba(255,255,255,.6);margin-right:6px}
.slider{position:relative;height:4px;border-radius:2px;background:rgba(255,255,255,.4);flex:1}
.slider .f{position:absolute;left:0;top:0;bottom:0;width:80%;border-radius:2px;background:#4CC2FF}
.slider .t{position:absolute;left:80%;top:50%;width:20px;height:20px;margin:-10px 0 0 -10px;border-radius:10px;background:#454545;border:1px solid rgba(255,255,255,.08);display:flex;align-items:center;justify-content:center}
.slider .t:after{content:"";width:12px;height:12px;border-radius:6px;background:#4CC2FF}
.ftr{display:flex;align-items:center;justify-content:space-between;padding:10px 14px;margin-top:4px;position:relative;background:rgba(0,0,0,.12);border-top:1px solid rgba(255,255,255,.05)}
.ftr .ver{position:absolute;left:50%;transform:translateX(-50%);font-size:11.5px;color:rgba(255,255,255,.5)}
"""

def body_C():
    st = "".join(f'<div class="st"><div class="k">{k.replace("FAN","fan")}</div><div class="v"><b>{v}</b><i>{u}</i></div><div class="bar"><div style="width:{p}%"></div></div></div>' for k, v, u, p in STATS)
    mem = (f'<div class="st"><div class="k">RAM<span class="clean">{F("broom",13)}Clean</span></div><div class="v"><b>13.5</b><i>/ 32 GB</i></div><div class="bar"><div style="width:42%"></div></div></div>'
           f'<div class="st"><div class="k">VRAM</div><div class="v"><b>1.7</b><i>/ 8 GB</i></div><div class="bar"><div style="width:21%"></div></div></div>')
    perf = "".join(f'<div class="seg tall{" on" if l==PERF_ON else ""}{" red" if l=="G-Mode" else ""}"><div class="ic">{F(n,20,l==PERF_ON)}</div>{l}</div>' for n, l in PERF)
    gpu = "".join(f'<div class="seg tall{" on" if l==GPU_ON else ""}"><div class="ic">{F(n,20,l==GPU_ON)}</div>{l}</div>' for n, l in GPU)
    cpu = "".join(f'<div class="seg{" on" if l=="Aggressive" else ""}">{l}</div>' for l in ["Off", "On", "Efficient", "Aggressive"])
    fan = '<div class="seg on">Auto</div><div class="seg">Max Boost</div><div class="seg dis">Custom curve…</div>'
    sw = "".join(f'<div class="sw{" sel" if i==0 else ""}" style="background:{c}"></div>' for i, c in enumerate(SWATCH)) + f'<div class="sw" style="background:{RAINBOW}"></div>'
    card = lambda ic, t, h, inner: f'<div class="card"><div class="sh"><b>{ic}{t}</b><span>{h}</span></div>{inner}</div>'
    return f"""<div class="win">
<div class="hdr"><div class="brand"><img src="{LOGO}"><b>Alien Controls</b><span>m15 R7</span></div><div class="hb"><div>{F('subtract',14)}</div><div>{F('dismiss',14)}</div></div></div>
<div class="body">
<div class="card"><div class="stats">{st}</div><div class="mem">{mem}</div>
<div class="pwr">{F('flash',14)}<span>CPU <b>38 W</b></span><span>·</span><span>GPU <b>72 W</b></span><span>·</span><span>Total <b>118 W</b></span></div></div>
{card(F('pulse',18),'Performance Mode','Balanced',f'<div class="track c5">{perf}</div><div class="row gk"><span style="flex:1">G-key (F1) toggles G-Mode</span><div class="tg" style="margin-right:10px"></div><div class="btn">Detect key</div></div>')}
{card(F('games',18),'GPU Mode','Standard · RTX 3070 Ti',f'<div class="track c4">{gpu}</div>')}
<div class="card"><div class="sh"><b>{F('developer_board',18)}CPU Boost</b><span>Plugged in</span></div><div class="track c4">{cpu}</div>
<div class="sh" style="margin:14px 0 10px"><b>{FAN(18)}Fans</b><span>BIOS controlled</span></div><div class="track c3">{fan}</div></div>
{card(F('lightbulb_filament',18),'Lighting','Brightness 80%',f'<div class="row" style="gap:8px">{sw}<div style="flex:1"></div><div class="btn">{F("power",13)}Off</div></div><div class="row" style="gap:14px;margin-top:12px"><div class="combo"><span><em>Effect</em>Static</span>{CHEV}</div><div class="row" style="flex:1.2;gap:8px"><div class="slider"><div class="f"></div><div class="t"></div></div><span style="font-size:11.5px;color:rgba(255,255,255,.62);width:28px;text-align:right">80%</span></div></div>')}
</div>
<div class="ftr"><div class="row" style="gap:10px"><div class="tg"></div><span style="font-size:12.5px">Run on startup</span></div><div class="ver">v0.7.0</div><div class="btn">{F('power',13)}Quit</div></div>
</div>"""


DIRS = {"A": ("refined-nebula", CSS_A, body_A, "#0b0d11"),
        "B": ("awcc-terminal", CSS_B, body_B, "#0b0d11"),
        "C": ("fluent-mica", CSS_C, body_C, "#0b0d11")}

if __name__ == "__main__":
    for key in (sys.argv[1:] or DIRS):
        slug, css, fn, bg = DIRS[key]
        html = page(css, fn(), bg)
        open(f"{WORK}/redesign/{key}-{slug}.html", "w").write(html)
        print(key, render(html, f"{OUT}/{key}-{slug}.png", 440))
