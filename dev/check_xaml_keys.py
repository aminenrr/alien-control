#!/usr/bin/env python3
"""2.0.0-beta1.4: resource-key check for the WPF themes (CI runs it, see .github/workflows/build.yml).

Fails (exit 1) when:
  * a XAML file declares the same x:Key twice (the beta1 crash: duplicate Card2 in Legend2.xaml),
  * a key is declared in more than one theme file (no exception in WPF, the later one silently wins),
  * a {StaticResource X} / {DynamicResource X} / BasedOn in any *.xaml, or a literal key passed to
    SetResourceReference / TryFindResource / FindResource / Resources["X"] in any *.cs, names a key that no theme
    file (or MainWindow.xaml's own resources) declares.
With --dead it also lists theme keys nothing reaches (transitively from MainWindow.xaml, App.xaml and the C# string
literals), which is how the 2.0.0-beta1.4 cleanup found the unused Legend.xaml keys.

Usage: python dev/check_xaml_keys.py [--dead] [repo root]
"""
import os
import re
import sys

ROOT = next((a for a in sys.argv[1:] if not a.startswith("--")), os.path.join(os.path.dirname(__file__), ".."))
SRC = os.path.join(ROOT, "src", "AlienControl")
THEMES = ["Themes/Legend.xaml", "Themes/Legend2.xaml"]

KEY = re.compile(r'x:Key="([^"{]+)"')
REF = re.compile(r'\{(?:StaticResource|DynamicResource)\s+([^}\s]+)\s*\}')
CS_REF = re.compile(r'(?:SetResourceReference\([^,]+,\s*|TryFindResource\(|FindResource\(|Resources\[)"([^"]+)"')
CS_LIT = re.compile(r'"([A-Za-z][A-Za-z0-9_.]+)"')


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def all_files(ext):
    for d, _, files in os.walk(SRC):
        if os.sep + "bin" in d or os.sep + "obj" in d:
            continue
        for f in files:
            if f.endswith(ext):
                yield os.path.join(d, f)


def top_level_elements(text):
    """(key, element text) for each direct child of the ResourceDictionary that has an x:Key."""
    out = []
    body = text[text.index(">", text.index("<ResourceDictionary")) + 1:]
    i, n = 0, len(body)
    while i < n:
        lt = body.find("<", i)
        if lt < 0:
            break
        if body.startswith("<!--", lt):
            i = body.index("-->", lt) + 3
            continue
        if body.startswith("</ResourceDictionary", lt):
            break
        m = re.match(r"<([A-Za-z_][\w:.]*)", body[lt:])
        name = m.group(1)
        # find the end of this element (nesting by tag name; self-closing aware)
        depth, j = 0, lt
        tag_re = re.compile(r"<(/?)" + re.escape(name) + r"(?=[\s>/])[^>]*?(/?)>", re.S)
        for t in tag_re.finditer(body, lt):
            if t.group(1):
                depth -= 1
            elif not t.group(2):
                depth += 1
            if depth == 0:
                j = t.end()
                break
        el = body[lt:j]
        start_tag = el[: el.index(">") + 1]
        k = KEY.search(start_tag)
        out.append((k.group(1) if k else None, el))
        i = j
    return out


def main():
    errors, dead_mode = [], "--dead" in sys.argv
    declared = {}   # key -> file
    elements = {}   # key -> text
    for rel in THEMES:
        text = read(os.path.join(SRC, rel))
        keys = KEY.findall(text)
        seen = set()
        for k in keys:
            if k in seen:
                errors.append(f"{rel}: duplicate x:Key '{k}'")
            seen.add(k)
        for k in seen:
            if k in declared:
                errors.append(f"x:Key '{k}' is declared in both {declared[k]} and {rel}")
            declared[k] = rel
        for k, el in top_level_elements(text):
            if k:
                elements[k] = el
            else:
                elements[f"<implicit {len(elements)}>"] = el

    xaml_refs, cs_refs, cs_lits = {}, {}, set()
    for path in all_files(".xaml"):
        text = read(path)
        rel = os.path.relpath(path, SRC)
        if not any(rel.replace(os.sep, "/") == t for t in THEMES):
            for k in KEY.findall(text):
                if k in declared and not rel.endswith("App.xaml"):
                    errors.append(f"{rel}: x:Key '{k}' is also declared in {declared[k]}")
                declared.setdefault(k, rel)
        for r in REF.findall(text):
            xaml_refs.setdefault(r, rel)
    for path in all_files(".cs"):
        text = read(path)
        rel = os.path.relpath(path, SRC)
        for r in CS_REF.findall(text):
            cs_refs.setdefault(r, rel)
        cs_lits.update(CS_LIT.findall(text))

    for r, where in sorted({**xaml_refs, **cs_refs}.items()):
        if r.startswith("{") or r.startswith("x:"):
            continue
        if r not in declared:
            errors.append(f"{where}: resource '{r}' is not declared in any theme or XAML file")

    if dead_mode:
        reach, todo = set(), []
        roots = set()
        for path in all_files(".xaml"):
            rel = os.path.relpath(path, SRC).replace(os.sep, "/")
            if rel not in THEMES:
                roots.update(REF.findall(read(path)))
        roots.update(cs_refs)
        roots.update(k for k in declared if k in cs_lits)
        roots.update(k for k in elements if k.startswith("<implicit"))
        todo = [k for k in roots if k in elements]
        while todo:
            k = todo.pop()
            if k in reach:
                continue
            reach.add(k)
            todo.extend(r for r in REF.findall(elements[k]) if r in elements and r not in reach)
        dead = sorted(k for k in elements if k not in reach)
        print(f"{len(dead)} unreachable theme keys:")
        for k in dead:
            print(f"  {declared.get(k, '?')}: {k}")

    for e in errors:
        print(("::error::" if os.environ.get("GITHUB_ACTIONS") else "error: ") + e)
    print(f"{len(declared)} keys declared, {len(set(xaml_refs) | set(cs_refs))} referenced, {len(errors)} errors")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
