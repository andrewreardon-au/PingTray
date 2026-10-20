#!/usr/bin/env python3
"""Composes the Microsoft Store listing screenshots (1366x768, the Store minimum) from the README images in
../screenshots. Each image is enlarged about 2x with Lanczos smoothing (nearest-neighbour magnifies the ClearType
colour fringes into blocks and looks fuzzy), centred on the flat dark backdrop the tray and menu images already
use, under a one-line caption. Needs Pillow and Segoe UI (Windows). Writes store/listing/."""
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(os.path.dirname(HERE), "screenshots")
OUT = os.path.join(HERE, "listing")
W, H = 1366, 768
BACKDROP = (46, 46, 46)
CAPTION = (255, 255, 255)
FONT = r"C:\Windows\Fonts\segoeui.ttf"

SHOTS = [  # (source, scale, caption, output)
    ("PingTray-screenshot_tray.png", 2, "Live network latency in the system tray", "screenshot-1-tray.png"),
    ("PingTray-screenshot_menu.png", 2, "Right-click for Settings, About and Exit", "screenshot-2-menu.png"),
    ("PingTray-screenshot_settings.png", 1.4, "Pick a connection type, or set your own limits", "screenshot-3-settings.png"),
    ("PingTray-screenshot_about.png", 2, "Version details and command-line help", "screenshot-4-about.png"),
]

os.makedirs(OUT, exist_ok=True)
font = ImageFont.truetype(FONT, 40)
for name, scale, caption, out in SHOTS:
    im = Image.open(os.path.join(SRC, name)).convert("RGB")
    im = im.resize((round(im.width * scale), round(im.height * scale)), Image.LANCZOS)
    canvas = Image.new("RGB", (W, H), BACKDROP)
    d = ImageDraw.Draw(canvas)
    tw = d.textlength(caption, font=font)
    d.text(((W - tw) / 2, 30), caption, font=font, fill=CAPTION)
    bottom_of_text = 30 + d.textbbox((0, 0), caption, font=font)[3]
    top = bottom_of_text + 25               # below the caption
    room = H - top - 20                     # keep 20 px under the picture
    assert im.height <= room, (out, im.height, room)
    y = top + (room - im.height) // 2
    canvas.paste(im, ((W - im.width) // 2, y))
    canvas.save(os.path.join(OUT, out))
    print("wrote", out, im.width, "x", im.height, "at", scale, "x")
