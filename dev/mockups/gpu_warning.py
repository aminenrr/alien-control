import designs
from lib import *
from PIL import Image
M={"subtract":"minus","dismiss":"x","pulse":"activity","battery_saver":"battery-eco","weather_moon":"moon","scales":"scale","gauge":"gauge","fire":"flame","games":"device-gamepad-2","leaf_two":"leaf","arrow_swap":"arrows-exchange","rocket":"rocket","flash":"bolt","developer_board":"cpu","lightbulb_filament":"bulb","broom":"eraser","power":"power","laptop":"device-laptop","flash_auto":"sparkles"}
designs.GPU=[("leaf_two","iGPU only"),("arrow_swap","Optimus"),("rocket","dGPU only"),("flash","Ultimate")];designs.GPU_ON="Optimus"
APPS=[("Discord","412 MB"),("chrome","268 MB"),("Steam","96 MB")]
for theme in ["B","C"]:
    if theme=="B":
        designs.F=lambda n,s=20,filled=False: icon("tabler",M[n],s); designs.FAN=lambda s=18: icon("tabler","propeller",s)
        warn=icon("tabler","alert-triangle",18) if os.path.exists(f"{ICONS}/tabler/alert-triangle.svg") else ""
        rows="".join(f'<div style="display:flex;justify-content:space-between;padding:7px 10px;border-top:1px solid #1D2228;font-family:\'JetBrains Mono\',Consolas,monospace;font-size:11px;color:#C9D1D9"><span><span style="color:#FFB020">▸</span> {a}</span><span style="color:#6B7682">{m}</span></div>' for a,m in APPS)
        card=f'''<div style="position:absolute;inset:0;background:rgba(5,6,8,.72);display:flex;align-items:center;justify-content:center;z-index:9">
<div style="width:350px;background:#0C0E11;border:1px solid rgba(255,176,32,.45);border-radius:6px;box-shadow:0 0 0 1px rgba(0,0,0,.6),0 10px 30px rgba(0,0,0,.8);overflow:hidden">
<div style="height:2px;background:linear-gradient(90deg,#FFB020,#FF4D6A)"></div>
<div style="padding:14px 16px 10px;display:flex;gap:10px;align-items:center;color:#FFB020">{warn}<b style="font-size:12px;letter-spacing:2.2px">RTX IN USE</b></div>
<div style="padding:0 16px 12px;font-size:12.5px;color:#9AA4AE;line-height:1.45">Close these apps before switching to <span style="color:#E6EDF3">iGPU only</span>, or they may crash.</div>
<div style="margin:0 16px;border:1px solid #1D2228;border-radius:4px">{rows.replace('border-top:1px solid #1D2228;','',1)}</div>
<div style="display:flex;gap:8px;justify-content:flex-end;padding:14px 16px">
<div style="padding:7px 14px;border:1px solid #2A3038;border-radius:4px;font-size:12.5px;color:#C9D1D9">Cancel</div>
<div style="padding:7px 14px;border:1px solid rgba(0,229,255,.5);background:rgba(0,229,255,.08);border-radius:4px;font-size:12.5px;color:#00E5FF">Refresh</div>
<div style="padding:7px 14px;border:1px solid rgba(255,176,32,.55);background:rgba(255,176,32,.10);border-radius:4px;font-size:12.5px;color:#FFB020">Switch anyway</div></div></div></div>'''
        base=designs.page(designs.CSS_B+".win{position:relative}",designs.body_B(),"#0b0d11")
    else:
        designs.F=lambda n,s=20,filled=False: icon("fluent",n,s,filled); designs.FAN=lambda s=18: icon("phosphor","fan",s)
        warn=icon("fluent","warning",20) if os.path.exists(f"{ICONS}/fluent/warning.svg") else ""
        rows="".join(f'<div style="display:flex;justify-content:space-between;padding:8px 12px;font-size:12.5px;color:#E6E6E6;border-top:1px solid rgba(255,255,255,.06)"><span>{a}</span><span style="color:rgba(255,255,255,.55)">{m}</span></div>' for a,m in APPS)
        card=f'''<div style="position:absolute;inset:0;background:rgba(0,0,0,.55);display:flex;align-items:center;justify-content:center;z-index:9">
<div style="width:350px;background:#2B2B2B;border:1px solid rgba(255,255,255,.10);border-radius:8px;box-shadow:0 16px 40px rgba(0,0,0,.6);overflow:hidden">
<div style="padding:18px 20px 6px;display:flex;gap:10px;align-items:center;color:#FCE100">{warn}<b style="font-size:15px;font-weight:600;color:#fff">Apps are using the RTX</b></div>
<div style="padding:4px 20px 12px;font-size:12.5px;color:rgba(255,255,255,.72);line-height:1.45">Close them before switching to iGPU only, or they may crash.</div>
<div style="margin:0 20px;background:rgba(255,255,255,.04);border:1px solid rgba(255,255,255,.07);border-radius:6px">{rows.replace('border-top:1px solid rgba(255,255,255,.06)','',1)}</div>
<div style="display:flex;gap:8px;padding:16px 20px;background:#202020;margin-top:16px;border-top:1px solid rgba(255,255,255,.06)">
<div style="flex:1;text-align:center;padding:6px;background:#4CC2FF;border-radius:4px;font-size:12.5px;color:#000">Switch anyway</div>
<div style="flex:1;text-align:center;padding:6px;background:rgba(255,255,255,.08);border:1px solid rgba(255,255,255,.08);border-radius:4px;font-size:12.5px;color:#fff">Refresh</div>
<div style="flex:1;text-align:center;padding:6px;background:rgba(255,255,255,.08);border:1px solid rgba(255,255,255,.08);border-radius:4px;font-size:12.5px;color:#fff">Cancel</div></div></div></div>'''
        base=designs.page(designs.CSS_C+".win{position:relative}",designs.body_C(),"#0b0d11")
    html=base.replace('<div class="win">','<div class="win">'+card,1)
    f=f"{OUT}/gpu-warning-{theme}.png"; render(html,f,440)
    im=Image.open(f); w,h=im.size; m=h//2
    im.crop((0,m-560,w,m+560)).save(f"{OUT}/split/gpu-warning-{theme}.png"); print(theme,h)
