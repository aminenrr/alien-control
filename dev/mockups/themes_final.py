"""Legend (Tabler icons) and Fluent (Fluent icons) with the final GPU naming."""
import designs
from lib import *
designs.GPU=[("leaf_two","iGPU only"),("arrow_swap","Optimus"),("rocket","dGPU only"),("flash","Ultimate")]
designs.GPU_ON="Optimus"
# Fluent theme
designs.F=lambda n,s=20,filled=False: icon("fluent",n,s,filled)
c=designs.page(designs.CSS_C,designs.body_C(),"#0b0d11").replace("Standard · RTX 3070 Ti","Optimus · RTX 3070 Ti")
open(f"{WORK}/redesign/C-fluent-final.html","w").write(c); print(render(c,f"{OUT}/C-fluent-final.png",440))
# Legend theme
M={"subtract":"minus","dismiss":"x","pulse":"activity","battery_saver":"battery-eco","weather_moon":"moon","scales":"scale","gauge":"gauge","fire":"flame","games":"device-gamepad-2","leaf_two":"leaf","arrow_swap":"arrows-exchange","rocket":"rocket","flash":"bolt","developer_board":"cpu","lightbulb_filament":"bulb","broom":"eraser","power":"power","laptop":"device-laptop","flash_auto":"sparkles"}
designs.F=lambda n,s=20,filled=False: icon("tabler",M[n],s)
designs.FAN=lambda s=18: icon("tabler","propeller",s)
b=designs.page(designs.CSS_B,designs.body_B(),"#0b0d11").replace("STANDARD · 3070 Ti","OPTIMUS · 3070 Ti")
open(f"{WORK}/redesign/B-legend-final.html","w").write(b); print(render(b,f"{OUT}/B-legend-final.png",440))
from PIL import Image
for k in ["B-legend-final","C-fluent-final"]:
    im=Image.open(f"{OUT}/{k}.png");w,h=im.size;m=h//2
    im.crop((0,0,w,m+60)).save(f"{OUT}/split/{k}-1-top.png");im.crop((0,m-60,w,h)).save(f"{OUT}/split/{k}-2-bottom.png")
