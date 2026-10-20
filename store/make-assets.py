#!/usr/bin/env python3
"""Generates the MSIX / Store logo PNGs into store/Assets (requires Pillow).
Logo: three bottom-aligned bars of increasing height (green, orange, red)."""
import os
from PIL import Image, ImageDraw

SS = 8  # supersampling factor
BARS = [  # (colour, height fraction of the drawn area)
    ((0x2E, 0xCC, 0x40, 255), 0.45),
    ((0xFF, 0xA5, 0x00, 255), 0.72),
    ((0xDC, 0x26, 0x26, 255), 1.00),
]

def logo(size):
    big = size * SS
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    area = big * 0.80
    margin = (big - area) / 2
    gap = area * 0.08
    bw = (area - 2 * gap) / 3
    radius = bw * 0.28
    bottom = margin + area
    for i, (col, frac) in enumerate(BARS):
        x0 = margin + i * (bw + gap)
        d.rounded_rectangle([x0, bottom - area * frac, x0 + bw, bottom], radius=radius, fill=col)
    return img.resize((size, size), Image.LANCZOS)

OUT = {
    "StoreLogo.png": 50,
    "Square44x44Logo.png": 44,
    "Square150x150Logo.png": 150,
}

if __name__ == "__main__":
    d = os.path.join(os.path.dirname(os.path.abspath(__file__)), "Assets")
    os.makedirs(d, exist_ok=True)
    for name, size in OUT.items():
        logo(size).save(os.path.join(d, name))
        print("wrote", name)
    # The 300x300 Partner Center listing logo is uploaded in the Store listing, not packaged.
    l = os.path.join(os.path.dirname(d), "listing")
    os.makedirs(l, exist_ok=True)
    logo(300).save(os.path.join(l, "StoreListing_300.png"))
    print("wrote listing/StoreListing_300.png")
