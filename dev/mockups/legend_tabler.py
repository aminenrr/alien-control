import designs
from lib import *
M={"subtract":"minus","dismiss":"x","pulse":"activity","battery_saver":"battery-eco","weather_moon":"moon","scales":"scale","gauge":"gauge","fire":"flame","games":"device-gamepad-2","leaf_two":"leaf","laptop":"device-laptop","flash":"bolt","flash_auto":"sparkles","developer_board":"cpu","lightbulb_filament":"bulb","broom":"eraser","power":"power"}
designs.F=lambda n,s=20,filled=False: icon("tabler",M[n],s)
designs.FAN=lambda s=18: icon("tabler","propeller",s)
html=designs.page(designs.CSS_B,designs.body_B(),"#0b0d11")
open(f"{WORK}/redesign/B-legend-tabler.html","w").write(html)
print(render(html,f"{OUT}/B-legend-tabler.png",440))
