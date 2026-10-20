#!/usr/bin/env python3
"""Composes the Microsoft Store listing screenshots (1920x1080) from the README images in ../screenshots.
Each image is scaled by a whole number (crisp text), centred on the flat dark backdrop the tray and menu
images already use, under a one-line caption. Needs Pillow and Segoe UI (Windows). Writes store/listing/."""
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(os.path.dirname(HERE), "screenshots")
OUT = os.path.join(HERE, "listing")
W, H = 1920, 1080
BACKDROP = (46, 46, 46)
CAPTION = (255, 255, 255)
FONT = r"C:\Windows\Fonts\segoeui.ttf"

SHOTS = [  # (source, scale, caption, output)
    ("PingTray-screenshot_tray.png", 3, "Live network latency in the system tray", "screenshot-1-tray.png"),
    ("PingTray-screenshot_menu.png", 2, "Right-click for Settings, About and Exit", "screenshot-2-menu.png"),
    ("PingTray-screenshot_settings.png", 2, "Pick a connection type, or set your own limits", "screenshot-3-settings.png"),
    ("PingTray-screenshot_about.png", 3, "Version details and command-line help", "screenshot-4-about.png"),
]

os.makedirs(OUT, exist_ok=True)
font = ImageFont.truetype(FONT, 56)
for name, scale, caption, out in SHOTS:
    im = Image.open(os.path.join(SRC, name)).convert("RGB")
    im = im.resize((im.width * scale, im.height * scale), Image.NEAREST)
    canvas = Image.new("RGB", (W, H), BACKDROP)
    d = ImageDraw.Draw(canvas)
    tw = d.textlength(caption, font=font)
    d.text(((W - tw) / 2, 40), caption, font=font, fill=CAPTION)
    bottom_of_text = 40 + d.textbbox((0, 0), caption, font=font)[3]
    top = bottom_of_text + 30               # below the caption
    room = H - top - 35                     # keep 35 px under the picture
    assert im.height <= room, (out, im.height, room)
    y = top + (room - im.height) // 2
    canvas.paste(im, ((W - im.width) // 2, y))
    canvas.save(os.path.join(OUT, out))
    print("wrote", out, im.width, "x", im.height, "at", scale, "x")
