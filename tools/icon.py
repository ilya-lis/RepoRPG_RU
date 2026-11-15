#!/usr/bin/env python3
"""Draws mod/icon.png (256x256, the size Thunderstore asks for). Needs Pillow and a font with Cyrillic (DejaVu Sans Bold).

usage: icon.py <out.png>
"""
import sys
from PIL import Image, ImageDraw, ImageFont

FONT = '/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf'
S = 4                       # drawn four times larger, then scaled down: smooth edges
W = 256 * S


def font(size): return ImageFont.truetype(FONT, size * S)


def centered(d, text, y, f, fill, tracking=0):
    widths = [d.textlength(ch, font=f) for ch in text]
    total = sum(widths) + tracking * S * (len(text) - 1)
    x = (W - total) / 2
    for ch, w in zip(text, widths):
        d.text((x, y * S), ch, font=f, fill=fill)
        x += w + tracking * S


img = Image.new('RGB', (W, W), (18, 20, 26))
d = ImageDraw.Draw(img)
# background: a dark panel with a lighter frame, like a menu window
d.rounded_rectangle((10 * S, 10 * S, W - 10 * S, W - 10 * S), radius=26 * S, fill=(28, 31, 40), outline=(70, 76, 92), width=3 * S)
# the big letters
centered(d, 'РУ', 44, font(104), (255, 215, 90))
# an experience bar under them: three quarters full
x0, x1, y0, y1 = 44 * S, W - 44 * S, 166 * S, 178 * S
d.rounded_rectangle((x0, y0, x1, y1), radius=6 * S, fill=(52, 57, 70))
d.rounded_rectangle((x0, y0, x0 + (x1 - x0) * 0.75, y1), radius=6 * S, fill=(95, 200, 120))
# what it is for
centered(d, 'RepoRPG', 190, font(30), (225, 228, 235), tracking=1)
img.resize((256, 256), Image.LANCZOS).save(sys.argv[1], optimize=True)
