import designs, os
from lib import *
from PIL import Image
P=[("Battery","#FFB020"),("Quiet","#3CFF7A"),("Balanced","#00E5FF"),("Performance","#FF4D6A"),("Custom","#FFFFFF")]
def rgba(h,a):
    h=h.lstrip('#');return f"rgba({int(h[0:2],16)},{int(h[2:4],16)},{int(h[4:6],16)},{a})"
M={"subtract":"minus","dismiss":"x","pulse":"activity","battery_saver":"battery-eco","weather_moon":"moon","scales":"scale","gauge":"gauge","fire":"flame","games":"device-gamepad-2","leaf_two":"leaf","arrow_swap":"arrows-exchange","rocket":"rocket","flash":"bolt","developer_board":"cpu","lightbulb_filament":"bulb","broom":"eraser","power":"power","laptop":"device-laptop","flash_auto":"sparkles"}
designs.GPU=[("leaf_two","iGPU only"),("arrow_swap","Optimus"),("rocket","dGPU only"),("flash","Ultimate")];designs.GPU_ON="Optimus"
out={}
for theme in ["B","C"]:
    if theme=="B":
        designs.F=lambda n,s=20,filled=False: icon("tabler",M[n],s); designs.FAN=lambda s=18: icon("tabler","propeller",s)
        base=designs.page(designs.CSS_B+'.brand b,.brand span{white-space:nowrap}.brand{gap:8px}',designs.body_B(),"#0b0d11"); anchor='<span class="mono">m15 R7</span>'
    else:
        designs.F=lambda n,s=20,filled=False: icon("fluent",n,s,filled); designs.FAN=lambda s=18: icon("phosphor","fan",s)
        base=designs.page(designs.CSS_C,designs.body_C(),"#0b0d11"); anchor='<span>m15 R7</span>'
    crops=[]
    for name,c in P:
        if theme=="B":
            chip=f'<i style="font-style:normal;font-family:\'JetBrains Mono\',Consolas,monospace;font-size:9.5px;letter-spacing:.4px;white-space:nowrap;color:{c};border:1px solid {rgba(c,.4)};background:{rgba(c,.08)};border-radius:3px;padding:1px 5px;display:inline-flex;align-items:center;gap:5px"><b style="width:6px;height:6px;border-radius:50%;background:{c};display:inline-block;letter-spacing:0"></b>{name.upper()}</i>'
        else:
            chip=f'<i style="font-style:normal;font-size:11.5px;color:{c};border:1px solid {rgba(c,.35)};background:{rgba(c,.10)};border-radius:10px;padding:1px 8px 1px 6px;display:inline-flex;align-items:center;gap:5px"><b style="width:6px;height:6px;border-radius:50%;background:{c};display:inline-block"></b>{name}</i>'
        html=base.replace(anchor,anchor+chip,1)
        f=f"{WORK}/redesign/chip-{theme}-{name}.png"; render(html,f,440)
        im=Image.open(f); crops.append(im.crop((0,0,im.width,200 if theme=="B" else 170)))
    W=crops[0].width; H=sum(c.height for c in crops)+20*4
    sheet=Image.new("RGB",(W,H),"#0b0d11"); y=0
    for c in crops: sheet.paste(c,(0,y)); y+=c.height+20
    sheet.save(f"{OUT}/split/profile-chip-{theme}.png"); print(sheet.size)
