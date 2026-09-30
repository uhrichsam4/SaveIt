#!/usr/bin/env python3
"""Renders windows/SaveIt/Assets/SaveIt.ico: dark squircle, black pill, white down-arrow.
Run from the repo root: python3 windows/tools/make_icon.py (needs Pillow)."""
from PIL import Image, ImageDraw
import os

S = 1024

def render():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    # Squircle with a subtle vertical gradient.
    inset = int(S * 0.055)
    radius = int(S * 0.23)
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    top, bot = (58, 59, 66), (22, 22, 26)
    for y in range(S):
        t = y / (S - 1)
        c = tuple(int(top[i] + (bot[i] - top[i]) * t) for i in range(3))
        gd.line([(0, y), (S, y)], fill=c + (255,))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([inset, inset, S - inset, S - inset], radius=radius, fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    # Hairline highlight along the top edge of the squircle.
    d.rounded_rectangle([inset, inset, S - inset, S - inset], radius=radius, outline=(255, 255, 255, 34), width=int(S * 0.008))
    # Black island pill.
    pw, ph = S * 0.64, S * 0.34
    cx, cy = S / 2, S * 0.5
    d.rounded_rectangle([cx - pw / 2, cy - ph / 2, cx + pw / 2, cy + ph / 2], radius=ph / 2, fill=(0, 0, 0, 255))
    # White down arrow inside the pill.
    w = int(S * 0.052)
    ah = ph * 0.56
    y0, y1 = cy - ah / 2, cy + ah / 2
    d.line([(cx, y0), (cx, y1 - w * 0.3)], fill="white", width=w)
    chev = ah * 0.46
    d.line([(cx - chev, y1 - chev), (cx, y1)], fill="white", width=w, joint="curve")
    d.line([(cx, y1), (cx + chev, y1 - chev)], fill="white", width=w, joint="curve")
    for (px, py) in [(cx, y0), (cx - chev, y1 - chev), (cx + chev, y1 - chev), (cx, y1)]:
        d.ellipse([px - w / 2, py - w / 2, px + w / 2, py + w / 2], fill="white")
    return img

if __name__ == "__main__":
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, "..", "SaveIt", "Assets", "SaveIt.ico")
    big = render()
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    big.resize((256, 256), Image.LANCZOS).save(out, sizes=[(s, s) for s in sizes])
    print("wrote", os.path.normpath(out))
