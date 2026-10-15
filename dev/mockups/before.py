"""BEFORE: faithful HTML rebuild of repo/src/AlienControl/MainWindow.xaml + App.xaml (v0.7.0)."""
from lib import *

G = current_glyph
CSS = """
*{box-sizing:border-box;margin:0;padding:0}
body{background:#0b0d11;font-family:SegoeSub,sans-serif;width:560px;padding:10px}
.win{width:540px;border-radius:16px;border:1px solid rgba(255,255,255,.12);background:#1B1E24;box-shadow:0 0 18px rgba(0,0,0,.55);overflow:hidden}
.hdr{display:flex;align-items:center;justify-content:space-between;padding:10px 10px 10px 18px;border-bottom:1px solid rgba(255,255,255,.08);
 background:linear-gradient(135deg,#173038 0%,#1E2433 60%,#262038 100%)}
.brand{display:flex;align-items:center}
.brand img{height:24px}
.brand b{margin-left:12px;font-size:18px;font-weight:600;color:#F2F5F8}
.brand span{margin-left:12px;font-size:13px;color:#8A95A3;margin-top:2px}
.hb{display:flex}.hb div{width:38px;height:30px;display:flex;align-items:center;justify-content:center;color:#B8C2CC}
.body{padding:10px 14px 6px}
.g4{display:grid;grid-template-columns:repeat(4,1fr)}.g2{display:grid;grid-template-columns:repeat(2,1fr)}.g3{display:grid;grid-template-columns:repeat(3,1fr)}
.card{background:#23272E;border:1px solid #2E333B;border-radius:10px;padding:12px 14px;margin:4px}
.lbl{font-size:11px;color:#8A95A3}
.val{margin-top:2px;white-space:nowrap}.val b{font-size:22px;font-weight:600;color:#F2F5F8}.val i{font-style:normal;font-size:12px;color:#8A95A3;margin-left:3px}
.bar{height:3px;border-radius:2px;background:rgba(255,255,255,.08);margin-top:8px;overflow:hidden}.bar>div{height:100%;border-radius:2px;background:linear-gradient(90deg,#00E5FF,#7C4DFF)}
.sh{display:flex;justify-content:space-between;align-items:center;margin:0 4px 6px}
.sh b{font-size:15px;font-weight:600;color:#E8EDF2;display:flex;align-items:center;white-space:nowrap}
.sh b em{font-family:EmojiMono;font-style:normal;font-weight:400;margin-right:9px;font-size:14px}
.sh span{font-size:12.5px;color:#8A95A3}
.mb{background:#23272E;border:1px solid #2E333B;border-radius:12px;padding:14px 6px;margin:4px;text-align:center;color:#C9D2DC;font-size:13px}
.mb .gl{display:flex;justify-content:center;color:#B8C2CC;margin-bottom:8px}
.mb.on{border-color:#00D8F0;background:linear-gradient(180deg,rgba(0,229,255,.2),rgba(0,229,255,.063));color:#fff}.mb.on .gl{color:#00D8F0}
.sb{background:#23272E;border:1px solid #2E333B;border-radius:10px;padding:11px 6px;margin:4px;text-align:center;color:#C9D2DC;font-size:13px;white-space:nowrap}
.sb.on{border-color:#00D8F0;background:linear-gradient(180deg,rgba(0,229,255,.2),rgba(0,229,255,.063));color:#fff}
.sb.dis{opacity:.4}
.row{display:flex;align-items:center}
.btn{background:#23272E;border:1px solid #2E333B;border-radius:10px;padding:10px 16px;margin:4px 0 4px 8px;color:#C9D2DC;font-size:13px;white-space:nowrap}
.tg{width:40px;height:20px;border-radius:10px;background:#00D8F0;position:relative;flex:none}.tg:after{content:"";position:absolute;right:4px;top:4px;width:12px;height:12px;border-radius:6px;background:#111}
.tg.off{background:transparent;border:1px solid #9aa3ad}.tg.off:after{left:4px;right:auto;top:3px;background:#C9D2DC}
.sw{width:24px;height:24px;border-radius:7px;border:1px solid rgba(255,255,255,.19);margin:3px}
.combo{width:150px;margin-left:8px;align-self:stretch;margin-top:4px;margin-bottom:4px;border-radius:6px;background:rgba(255,255,255,.06);border:1px solid rgba(255,255,255,.07);border-bottom-color:rgba(255,255,255,.2);
 display:flex;align-items:center;justify-content:space-between;padding:0 11px;font-size:13px;color:#E8EDF2}
.slider{position:relative;height:4px;border-radius:2px;background:rgba(255,255,255,.35);margin:20px 12px 8px}
.slider .f{position:absolute;left:0;top:0;bottom:0;width:80%;background:#00D8F0;border-radius:2px}
.slider .t{position:absolute;left:80%;top:50%;width:20px;height:20px;margin:-10px 0 0 -10px;border-radius:10px;background:#454545;display:flex;align-items:center;justify-content:center}
.slider .t:after{content:"";width:12px;height:12px;border-radius:6px;background:#00D8F0}
.status{font-size:11.5px;color:#8A95A3;margin:8px 4px 0}
.ftr{border-top:1px solid rgba(255,255,255,.08);padding:8px 18px;display:flex;align-items:center;justify-content:space-between;position:relative}
.ftr .ver{position:absolute;left:50%;transform:translateX(-50%);font-size:12px;color:#8A95A3}
"""

def stat(label, v, unit, pct):
    return f'<div class="card"><div class="lbl">{label}</div><div class="val"><b>{v}</b><i>{unit}</i></div><div class="bar"><div style="width:{pct}%"></div></div></div>'

def mb(code, label, on=False, red=False):
    return f'<div class="mb{" on" if on else ""}"><div class="gl">{G(code,20)}</div>{label}</div>'

chev = '<svg width="12" height="12" viewBox="0 0 12 12"><path d="M2.5 4.5 6 8l3.5-3.5" fill="none" stroke="#C9D2DC" stroke-width="1.2"/></svg>'
html = f"""<!doctype html><html><head><meta charset="utf-8"><style>{FONT_CSS}{CSS}</style></head><body><div class="win">
<div class="hdr"><div class="brand"><img src="{LOGO}"><b>Alien Controls</b><span>·&nbsp; m15 R7</span></div>
<div class="hb"><div>{G('E921',13)}</div><div>{G('E8BB',13)}</div></div></div>
<div class="body">
<div class="g4">{stat('CPU','62','°C',62)}{stat('CPU FAN','41','%',41)}{stat('GPU','58','°C',58)}{stat('GPU FAN','38','%',38)}</div>
<div class="g2" style="margin-bottom:10px">{stat('RAM&nbsp; · &nbsp;click to clean','13.5','/ 32 GB',42)}{stat('VRAM','1.7','/ 8 GB',21)}</div>
<div class="sh"><b><em>⚡</em>Performance Mode</b><span>Balanced</span></div>
<div class="g4" style="margin-bottom:6px">{mb('E708','Quiet')}{mb('E7B8','Balanced',True)}{mb('E945','Performance')}{mb('ECAD','G-Mode')}</div>
<div class="row" style="margin:0 4px 10px"><div style="flex:1;font-size:12px;color:#8A95A3">G-key (F1) toggles G-Mode · key 0x80</div><div class="tg" style="margin-right:10px"></div><div class="btn" style="padding:6px 12px;margin:0">Detect key</div></div>
<div class="sh"><b><em>🎮</em>GPU Mode</b><span>Standard · RTX 3070 Ti</span></div>
<div class="g4" style="margin-bottom:10px">{mb('EC0A','Eco')}{mb('E7F4','Standard',True)}{mb('E945','Ultimate')}{mb('E83E','Optimized')}</div>
<div class="sh"><b><em>🧠</em>CPU Boost</b><span>Plugged in</span></div>
<div class="g4" style="margin-bottom:8px"><div class="sb">Off</div><div class="sb">On</div><div class="sb">Efficient</div><div class="sb on">Aggressive</div></div>
<div class="sh"><b><em>🌀</em>Fans</b><span>BIOS controlled</span></div>
<div class="g3" style="margin-bottom:8px"><div class="sb on">Auto</div><div class="sb">Max Boost</div><div class="sb dis">Custom curve…</div></div>
<div class="sh"><b><em>💡</em>Lighting</b><span>Brightness 80%</span></div>
<div class="row"><div class="card row" style="flex:1;padding:6px 10px"><span style="margin:0 10px 0 4px;color:#C9D2DC;font-size:13px">Color</span>
<div class="sw" style="background:#00E5FF"></div><div class="sw" style="background:#7C4DFF"></div><div class="sw" style="background:#FF4D6A"></div><div class="sw" style="background:#3CFF7A"></div><div class="sw" style="background:#fff"></div><div class="sw" style="background:linear-gradient(135deg,#f00,#ff0 20%,#0f0 40%,#0ff 60%,#00f 80%,#f0f)"></div></div>
<div class="combo"><span>Static</span>{chev}</div><div class="btn">Off</div></div>
<div class="slider"><div class="f"></div><div class="t"></div></div>
<div class="status">Done.</div>
</div>
<div class="ftr"><div class="row"><div class="tg"></div><span style="margin-left:10px;color:#C9D2DC;font-size:13px">Run on startup</span></div><div class="ver">v0.7.0</div><div class="btn" style="padding:7px 18px;margin:0">Quit</div></div>
</div></body></html>"""
open(f"{WORK}/redesign/before.html", "w").write(html)
print(render(html, f"{OUT}/before-current.png", 560))
