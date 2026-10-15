from lib import *

slots = MAP["slots"]
fluent_enum = {"subtract": "Subtract24", "dismiss": "Dismiss24", "pulse": "Pulse24", "battery_saver": "BatterySaver24",
               "weather_moon": "WeatherMoon24", "scales": "Scales24", "gauge": "Gauge24", "fire": "Fire24", "games": "Games24",
               "leaf_two": "LeafTwo24", "laptop": "Laptop24", "flash": "Flash24", "flash_auto": "FlashAuto24",
               "developer_board": "DeveloperBoard24", "weather_squalls": "WeatherSqualls24",
               "lightbulb_filament": "LightbulbFilament24", "broom": "Broom24", "window_arrow_up": "WindowArrowUp24", "power": "Power24"}

# current icons: ("mdl", code, name) | ("emoji", char, name) | None
current = {
    "Minimize": ("mdl", "E921", "E921 ChromeMinimize"), "Close": ("mdl", "E8BB", "E8BB ChromeClose"),
    "Performance Mode": ("emoji", "\u26A1", "U+26A1 emoji"), "Battery": None,
    "Quiet": ("mdl", "E708", "E708 QuietHours"), "Balanced": ("mdl", "E7B8", "E7B8 Package"),
    "Performance": ("mdl", "E945", "E945 LightningBolt"), "G-Mode": ("mdl", "ECAD", "ECAD Calories"),
    "GPU Mode": ("emoji", "\U0001F3AE", "U+1F3AE emoji"), "Eco": ("mdl", "EC0A", "EC0A Sustainable"),
    "Standard": ("mdl", "E7F4", "E7F4 TVMonitor"), "Ultimate": ("mdl", "E945", "E945 (dup!)"),
    "Optimized": ("mdl", "E83E", "E83E BatteryCharging9"), "CPU Boost": ("emoji", "\U0001F9E0", "U+1F9E0 emoji"),
    "Fans": ("emoji", "\U0001F300", "U+1F300 emoji"), "Lighting": ("emoji", "\U0001F4A1", "U+1F4A1 emoji"),
    "RAM clean": None, "Tray: Open": None, "Tray: Quit": None,
}
groups = {"Minimize": "Header", "Performance Mode": "Section titles", "Battery": "Performance buttons",
          "Eco": "GPU buttons", "RAM clean": "Other"}
section_slots = ["Performance Mode", "GPU Mode", "CPU Boost", "Fans", "Lighting"]
order = ["Minimize", "Close", "Performance Mode", "GPU Mode", "CPU Boost", "Fans", "Lighting",
         "Battery", "Quiet", "Balanced", "Performance", "G-Mode", "Eco", "Standard", "Ultimate", "Optimized",
         "RAM clean", "Tray: Open", "Tray: Quit"]
groups = {"Minimize": "HEADER", "Performance Mode": "SECTION TITLES", "Battery": "PERFORMANCE MODE BUTTONS",
          "Eco": "GPU MODE BUTTONS", "RAM clean": "OTHER"}

cols = [("fluent", "Fluent System Icons", "MIT · Microsoft", "built into WPF-UI (SymbolIcon) — no new dependency"),
        ("lucide", "Lucide", "ISC · Lucide contributors", "stroke SVG → Path data in XAML"),
        ("phosphor", "Phosphor (regular)", "MIT · Phosphor Icons", "fill SVG → Path data in XAML"),
        ("tabler", "Tabler (outline)", "MIT · Paweł Kuna", "stroke SVG → Path data in XAML")]

def cell(inner, cap, cls=""):
    return f'<td class="{cls}"><div class="ic">{inner}</div><div class="cap">{cap}</div></td>'

rows = ""
for s in order:
    if s in groups:
        rows += f'<tr class="grp"><td colspan="6">{groups[s]}</td></tr>'
    c = current[s]
    if c is None:
        cur = cell('<span class="none">—</span>', "none today" if s != "Battery" else "new button")
    elif c[0] == "mdl":
        cur = cell(current_glyph(c[1], 22), c[2], "warn" if "dup" in c[2] else "")
    else:
        cur = cell(f'<span class="emo">{c[1]}</span>', c[2] + ", drawn mono")
    tds = ""
    for key, *_ in cols:
        n = MAP["map"][key][s]
        cap = fluent_enum.get(n, n) if key == "fluent" else n
        cls = ""
        if key == "fluent" and n == "weather_squalls":
            cap += " (no fan glyph)"; cls = "warn"
        tds += cell(slot_icon(key, s, 22), cap, cls)
    rows += f'<tr><th>{s}</th>{cur}{tds}</tr>'

head = '<tr class="hd"><th>Slot</th><td><b>Current</b><small>Segoe Fluent Icons glyph / emoji</small><small>&nbsp;</small></td>' + "".join(
    f'<td class="{"rec" if k=="fluent" else ""}"><b>{t}{" ★" if k=="fluent" else ""}</b><small>{l}</small><small>{d}</small></td>' for k, t, l, d in cols) + "</tr>"

html = f"""<!doctype html><html><head><meta charset="utf-8"><style>{FONT_CSS}
*{{box-sizing:border-box;margin:0;padding:0}}
body{{background:#14161B;font-family:SegoeSub,sans-serif;color:#E8EDF2;padding:28px;width:1180px}}
h1{{font-size:22px;font-weight:600}} .sub{{color:#8A95A3;font-size:13px;margin:4px 0 18px}}
table{{border-collapse:separate;border-spacing:0;width:100%;background:#1B1E24;border:1px solid #2E333B;border-radius:14px;overflow:hidden}}
th,td{{padding:9px 10px;border-bottom:1px solid #252A31;text-align:center;vertical-align:middle}}
th{{text-align:left;font-weight:600;font-size:13px;color:#C9D2DC;width:150px}}
tr.hd td{{font-size:13px;vertical-align:top;background:#20242B}} tr.hd td b{{display:block;font-weight:600;color:#F2F5F8}}
tr.hd small{{display:block;color:#8A95A3;font-size:11px;margin-top:2px;line-height:1.3}}
tr.hd td.rec{{background:linear-gradient(180deg,#0f3a44,#20242B)}} tr.hd td.rec b{{color:#5FF0FF}}
tr.grp td{{text-align:left;font-size:10.5px;letter-spacing:1.4px;color:#5FD8EC;background:#171A1F;padding:6px 12px}}
.ic{{display:flex;justify-content:center;align-items:center;height:26px;color:#C9D2DC}}
.cap{{font-size:10.5px;color:#8A95A3;margin-top:3px;white-space:nowrap}}
td.warn .cap{{color:#FFB36B}}
.emo{{font-family:EmojiMono;font-size:20px;line-height:1;color:#C9D2DC}}
.none{{color:#4A525D;font-size:18px}}
.note{{color:#8A95A3;font-size:12px;margin-top:14px;line-height:1.5}}
</style></head><body>
<h1>Alien Controls — icon set comparison</h1>
<div class="sub">Rows = every icon slot in the UI. Rendered on the app's own background (#1B1E24 card) at 22 px, tinted with the app's icon grey #C9D2DC. ★ = recommended.</div>
<table>{head}{rows}</table>
<div class="note">Current column: the real Segoe Fluent Icons glyphs (Microsoft docs reference images) for each code in MainWindow.xaml, and section emoji drawn monochrome the way WPF renders them (WPF can't draw colour emoji).
Orange captions: Performance and Ultimate share the same glyph today (E945); Fluent System Icons has no fan glyph, so Fans would use WeatherSqualls24 or one embedded Path (Phosphor “fan”, MIT).<br>
Sources: Fluent: @fluentui/svg-icons + WPF-UI 3.0.5 SymbolRegular.cs (all 19 names verified) · Lucide: lucide-static · Phosphor: @phosphor-icons/core · Tabler: @tabler/icons (via unpkg). Licences checked in each GitHub repo's LICENSE file.</div>
</body></html>"""
open(f"{WORK}/redesign/icon_sheet.html", "w").write(html)
print(render(html, f"{OUT}/icon-comparison.png", 1180, scale=2.5))
