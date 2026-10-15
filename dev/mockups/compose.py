from PIL import Image, ImageDraw, ImageFont
from lib import OUT, FONTS
S = 2.4

before = Image.open(f"{OUT}/before-current.png").convert("RGB")
dirs = [("A-refined-nebula", "Direction A · Refined Nebula"),
        ("B-awcc-terminal", "Direction B · AWCC Terminal"),
        ("C-fluent-mica", "Direction C · Fluent Mica")]
fb = ImageFont.truetype(f"{FONTS}/sel/selawksb.ttf", int(26*S))
fs = ImageFont.truetype(f"{FONTS}/sel/selawk.ttf", int(17*S))
for slug, title in dirs:
    after = Image.open(f"{OUT}/{slug}.png").convert("RGB")
    pad, gap, top = int(40*S), int(60*S), int(96*S)
    W = pad * 2 + before.width + gap + after.width
    H = top + max(before.height, after.height) + pad
    c = Image.new("RGB", (W, H), "#0b0d11")
    d = ImageDraw.Draw(c)
    x2 = pad + before.width + gap
    d.text((pad, int(26*S)), "BEFORE", font=fb, fill="#8A95A3")
    d.text((pad, int(60*S)), "v0.7.0 as built today (rebuilt from MainWindow.xaml, 560 DIP wide)", font=fs, fill="#6B7684")
    d.text((x2, int(26*S)), "AFTER", font=fb, fill="#3FE6FF")
    d.text((x2, int(60*S)), f"{title} (440 DIP wide, + Battery mode & power line)", font=fs, fill="#8A95A3")
    c.paste(before, (pad, top)); c.paste(after, (x2, top))
    d.line([(pad + before.width + gap // 2, top), (pad + before.width + gap // 2, H - pad)], fill="#1C2028", width=int(2*S))
    c.save(f"{OUT}/before-after-{slug}.png")
    print(slug, c.size)
