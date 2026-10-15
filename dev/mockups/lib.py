"""Shared helpers for the Alien Controls redesign mocks (fonts, icons, rendering)."""
import base64, io, json, os, re
from PIL import Image

import os
ROOT = os.environ.get("ALIEN_MOCK_ROOT", os.path.abspath(os.path.join(os.path.dirname(__file__), "out")))  # icons in ROOT/work/icons, renders in ROOT/mock/redesign
WORK = f"{ROOT}/work"
ICONS = f"{WORK}/icons"
FONTS = f"{WORK}/fonts"
OUT = f"{ROOT}/mock/redesign"
os.makedirs(OUT, exist_ok=True)


def b64(path):
    return base64.b64encode(open(path, "rb").read()).decode()


FONT_CSS = f"""
@font-face{{font-family:'SegoeSub';src:url(data:font/ttf;base64,{b64(FONTS+'/sel/selawk.ttf')});font-weight:400}}
@font-face{{font-family:'SegoeSub';src:url(data:font/ttf;base64,{b64(FONTS+'/sel/selawksb.ttf')});font-weight:600}}
@font-face{{font-family:'SegoeSub';src:url(data:font/ttf;base64,{b64(FONTS+'/sel/selawkb.ttf')});font-weight:700}}
@font-face{{font-family:'SegoeSub';src:url(data:font/ttf;base64,{b64(FONTS+'/sel/selawksl.ttf')});font-weight:300}}
@font-face{{font-family:'CascMono';src:url(data:font/woff2;base64,{b64(FONTS+'/cascadia-400.woff2')});font-weight:400}}
@font-face{{font-family:'CascMono';src:url(data:font/woff2;base64,{b64(FONTS+'/cascadia-600.woff2')});font-weight:600}}
@font-face{{font-family:'CascMono';src:url(data:font/woff2;base64,{b64(FONTS+'/cascadia-700.woff2')});font-weight:700}}
html,body,*{{text-rendering:geometricPrecision}}
@font-face{{font-family:'EmojiMono';src:url(data:font/ttf;base64,{b64(FONTS+'/NotoEmoji.ttf')})}}
"""

LOGO = "data:image/png;base64," + b64(f"{ROOT}/repo/src/AlienControl/Assets/logo.png")

MAP = json.load(open(f"{ICONS}/map.json"))


def _clean(svg):
    svg = re.sub(r"<!--.*?-->", "", svg, flags=re.S).strip()
    head, rest = svg.split(">", 1)
    head = re.sub(r'\s(width|height|class)="[^"]*"', "", head)
    svg = head + ">" + rest
    svg = re.sub(r"<svg\s+", "<svg ", svg, count=1)
    return svg


def icon(set_, name, size=20, filled=False, extra=""):
    """Inline SVG coloured by CSS currentColor."""
    folder = "fluent_filled" if (filled and set_ == "fluent") else set_
    svg = _clean(open(f"{ICONS}/{folder}/{name}.svg").read())
    if set_ == "fluent":
        svg = svg.replace("<svg ", '<svg fill="currentColor" ', 1)
    svg = svg.replace("<svg ", f'<svg width="{size}" height="{size}" style="display:block;flex:none{extra}" ', 1)
    return svg


def slot_icon(set_, slot, size=20, filled=False):
    return icon(set_, MAP["map"][set_][slot], size, filled)


def current_glyph(code, size=20):
    """Segoe Fluent Icons glyph (official MS docs PNG) as a CSS mask, coloured by currentColor."""
    im = Image.open(f"{ICONS}/current/{code.lower()}.png").convert("RGBA")
    px = im.load()
    out = Image.new("RGBA", im.size)
    o = out.load()
    for y in range(im.size[1]):
        for x in range(im.size[0]):
            r, g, b, a = px[x, y]
            lum = (r + g + b) / 3
            alpha = int((255 - lum) * a / 255)
            o[x, y] = (255, 255, 255, alpha)
    buf = io.BytesIO(); out.save(buf, "PNG")
    uri = "data:image/png;base64," + base64.b64encode(buf.getvalue()).decode()
    return (f'<span style="display:block;flex:none;width:{size}px;height:{size}px;background:currentColor;'
            f'-webkit-mask:url({uri}) center/contain no-repeat"></span>')


def render(html, path, width, scale=3, full_page=True):
    from playwright.sync_api import sync_playwright
    with sync_playwright() as p:
        b = p.chromium.launch(executable_path="/usr/bin/chromium")
        pg = b.new_page(viewport={"width": width, "height": 400}, device_scale_factor=scale)
        pg.set_content(html, wait_until="load")
        pg.wait_for_timeout(300)
        # report any text overflow (scrollWidth > clientWidth) for QA
        over = pg.evaluate("""() => [...document.querySelectorAll('*')].filter(e => {
            const cs = getComputedStyle(e);
            return e.children.length===0 && e.textContent.trim() && e.clientWidth>0 && (e.scrollWidth > e.clientWidth+1);
          }).map(e => e.textContent.trim().slice(0,30))""")
        pg.screenshot(path=path, full_page=full_page, omit_background=False)
        b.close()
    return over
