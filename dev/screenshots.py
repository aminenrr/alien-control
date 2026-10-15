# Crops / centers the README screenshots (window + toasts) and rebuilds the plug / unplug toasts. Usage: python3 dev/screenshots.py <folder with the .webp shots>/
from PIL import Image, ImageDraw, ImageFont, ImageFilter
import numpy as np, cairosvg, io
U=__import__("sys").argv[1] if len(__import__("sys").argv)>1 else "./"
FB='/tmp/cc/ttf/static/CascadiaMono-Bold.ttf'; FR='/tmp/cc/ttf/static/CascadiaMono-Regular.ttf'
BG=(13,18,23)

def card_bbox(im):
    a=np.asarray(im.convert('RGB')).astype(int)
    m=(a.max(2)<45)&(a[:,:,2]-a[:,:,0]<14)
    R=np.where(m.sum(1)>200)[0]; C=np.where(m.sum(0)>40)[0]
    return C[0]-5, R[0]-2, C[-1]+3, R[-1]+3

def rounded(im, r):
    m=Image.new('L',im.size,0); ImageDraw.Draw(m).rounded_rectangle((0,0,im.width-1,im.height-1),r,fill=255)
    o=im.convert('RGBA'); o.putalpha(m); return o

def present(card, pad=28, r=8):
    card=rounded(card,r)
    W,H=card.width+2*pad, card.height+2*pad
    sh=Image.new('RGBA',(W,H),(0,0,0,0))
    ImageDraw.Draw(sh).rounded_rectangle((pad,pad+6,pad+card.width,pad+card.height+6),r,fill=(0,0,0,150))
    sh=sh.filter(ImageFilter.GaussianBlur(10))
    sh.alpha_composite(card,(pad,pad)); return sh

def crop_card(f):
    im=Image.open(U+f).convert('RGB'); b=card_bbox(im); return im.crop(b)

def text_box(card, pred):
    a=np.asarray(card).astype(int); m=pred(a)
    ys,xs=np.where(m); return xs.min(),ys.min(),xs.max(),ys.max()

def gray(a):
    return (a.max(2)-a.min(2)<28)&(a.max(2)>55)&(a.max(2)<170)
def tinted(a):
    return (a.max(2)-a.min(2)>90)

def icon(name_path, size, col):
    svg=f'<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24"><path d="{name_path}" fill="none" stroke="rgb{col}" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/></svg>'
    return Image.open(io.BytesIO(cairosvg.svg2png(bytestring=svg.encode(),output_width=size,output_height=size))).convert('RGBA')

BAT="M4 9a2 2 0 0 1 2 -2h11a2 2 0 0 1 2 2v.5a.5 .5 0 0 0 .5 .5a.5 .5 0 0 1 .5 .5v3a.5 .5 0 0 1 -.5 .5a.5 .5 0 0 0 -.5 .5v.5a2 2 0 0 1 -2 2h-5.5 M3 16.143c0 -2.84 2.09 -5.143 4.667 -5.143h2.333v.857c0 2.84 -2.09 5.143 -4.667 5.143h-2.333v-.857 M3 20v-3"

def draw_text(d, x, captop, s, font, col):
    # place so the cap top lands at captop
    bb=d.textbbox((0,0),'H',font=font)
    d.text((x, captop-bb[1]), s, font=font, fill=col)

def fit_font(path, target_w, s):
    for sz in [x/4 for x in range(30,120)]:
        f=ImageFont.truetype(path, sz)
        if f.getlength(s)>=target_w: return f
    return f

def resub(card, new, interior_left):
    card=card.copy(); a=np.asarray(card).astype(int)
    m=gray(a); m[:, :interior_left]=False
    ys,xs=np.where(m); x0,y0,x1,y1=xs.min(),ys.min(),xs.max(),ys.max()
    return card,(x0,y0,x1,y1)

out={}
# --- real toasts ---
for f,n in [('perf.webp','toast-performance'),('balance.webp','toast-balanced'),('g mode off .webp','toast-gmode-off'),('g mode on.webp','toast-gmode-on')]:
    out[n]=crop_card(f)

# --- synthesized: unplug (Battery) from the G-Mode-off (Quiet) card, positions measured on that crop ---
q=out['toast-gmode-off'].copy(); a=np.asarray(q).astype(int)
t=(a.max(2)>170)&(a.max(2)-a.min(2)>100); t[:, :12]=False; t[:, :60]=False
ys,xs=np.where(t); title_x=xs.min(); ttop=ys.min(); tw=xs.max()-xs.min()+1; ty0,ty1=ys.min(),ys.max()
icon_box=(26,20,45,39)
g=gray(a); g[:, :12]=False; g[:ty1+3,:]=False; g[-4:,:]=False
ys,xs=np.where(g); sx0,sy0,sx1,sy1=xs.min(),ys.min(),xs.max(),ys.max()
ftitle=fit_font(FB, tw, "QUIET"); fsub=fit_font(FR, sx1-sx0+1, "F1 · G-MODE OFF · BACK TO QUIET")
print('title',title_x,ttop,ftitle.size,'sub',sx0,sy0,fsub.size)

def battery_like(base, tint, iconpath, title, sub, bar_from):
    c=base.copy(); d=ImageDraw.Draw(c)
    d.rectangle((icon_box[0]-3, icon_box[1]-3, c.width-14, max(ty1,icon_box[3])+3), fill=BG)
    d.rectangle((sx0-2, sy0-3, c.width-14, sy1+3), fill=BG)
    # recolor bar: pixels in first 10 cols that are tinted
    px=c.load()
    for x in range(0,10):
        for y in range(c.height):
            r,g,b=px[x,y]
            if max(r,g,b)-min(r,g,b)>60:
                k=max(r,g,b)/255
                px[x,y]=tuple(int(v*k) for v in tint)
    isz=icon_box[3]-icon_box[1]+9
    ic=icon(iconpath, isz, tint)
    c=c.convert('RGBA'); c.alpha_composite(ic,(icon_box[0]-2, (icon_box[1]+icon_box[3])//2-isz//2))
    c=c.convert('RGB'); d=ImageDraw.Draw(c)
    draw_text(d, title_x, ttop, title, ftitle, tint)
    draw_text(d, sx0, sy0, sub, fsub, (96,106,118))
    return c

out['toast-unplug']=battery_like(q,(255,176,32),BAT,"BATTERY","UNPLUGGED · 60 HZ, POWER SAVING",None)

# --- plug in: the Balanced card without pills, new subtitle ---
b=out['toast-balanced'].copy(); a=np.asarray(b).astype(int)
m=gray(a); m[:, :12]=False; m[:4,:]=False; m[-4:,:]=False
ys,xs=np.where(m); bx0,by0,bx1,by1=xs.min(),ys.min(),xs.max(),ys.max()
d=ImageDraw.Draw(b)
# pills live right of the title on the title row: clear everything right of 60% width on the title row
mt=tinted(a); mt[:, :12]=False; ys,xs=np.where(mt)
row0,row1=ys.min(),ys.max()
d.rectangle((int(b.width*0.62), row0-4, b.width-12, row1+4), fill=BG)
d.rectangle((bx0-2,by0-3,b.width-14,by1+3), fill=BG)
fsb=fit_font(FR, bx1-bx0+1, "F2 · NEXT: PERFORMANCE")
draw_text(d, bx0, by0, "PLUGGED IN · BACK TO BALANCED", fsb, (96,106,118))
out['toast-plug']=b

for n,c in out.items():
    present(c).save(f'{n}.png')

# --- windows ---
def crop_window(f):
    im=Image.open(U+f).convert('RGB'); a=np.asarray(im).astype(int)
    m=(a.max(2)<60)&(a[:,:,2]-a[:,:,0]<25)
    R=np.where(m.sum(1)>im.width*0.6)[0]; C=np.where(m.sum(0)>im.height*0.6)[0]
    return im.crop((C[0]-1,R[0]-1,C[-1]+2,R[-1]+2))
for f,n in [('legend .webp','legend'),('fluent.webp','fluent')]:
    w=crop_window(f); print(n,w.size)
    present(w,pad=36,r=12).save(f'{n}.png')
