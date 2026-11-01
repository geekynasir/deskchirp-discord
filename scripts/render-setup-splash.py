#!/usr/bin/env python3
# Renders assets/brand/setup-splash.png. Needs Pillow and the Inter font (OFL):
#   python3 scripts/render-setup-splash.py assets/discord/deskchirp.png <Inter ttf folder> assets/brand/setup-splash.png
# Install splash for Velopack: Windows 11 dark surface, brand mark, title, progress area at bottom.
import sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter
logo_path, fonts, out = sys.argv[1], sys.argv[2], sys.argv[3]
S = 2  # draw at 2x, downsample for clean edges
W, H = 600, 336
img = Image.new("RGB", (W*S, H*S), (32, 32, 32))
# Mica-like tint: soft brand glows over the dark base.
glow = Image.new("RGB", (W*S, H*S), (0, 0, 0))
g = ImageDraw.Draw(glow)
g.ellipse((-160*S, -220*S, 360*S, 260*S), fill=(64, 66, 160))
g.ellipse((380*S, 150*S, 820*S, 560*S), fill=(30, 92, 84))
glow = glow.filter(ImageFilter.GaussianBlur(120*S))
img = Image.blend(img, glow, 0.55)
d = ImageDraw.Draw(img)
logo = Image.open(logo_path).convert("RGBA").resize((112*S, 112*S), Image.LANCZOS)
lx, ly = 56*S, 92*S
shadow = Image.new("RGBA", logo.size, (0, 0, 0, 0))
shadow.putalpha(logo.getchannel("A").point(lambda a: a * 0.45))
shadow = shadow.filter(ImageFilter.GaussianBlur(10*S))
img.paste(shadow, (lx, ly + 6*S), shadow)
img.paste(logo, (lx, ly), logo)
title = ImageFont.truetype(f"{fonts}/InterDisplay-SemiBold.ttf", 40*S)
body = ImageFont.truetype(f"{fonts}/Inter-Medium.ttf", 17*S)
caption = ImageFont.truetype(f"{fonts}/Inter-Regular.ttf", 14*S)
tx = 196*S
d.text((tx, 104*S), "Deskchirp", font=title, fill=(255, 255, 255))
d.text((tx, 158*S), "for Discord", font=body, fill=(200, 202, 255))
d.text((tx, 186*S), "Share what you’re building with friends.", font=caption, fill=(190, 190, 190))
d.text((56*S, 278*S), "Installing…", font=caption, fill=(220, 220, 220))
# Hairline frame like a Windows 11 surface edge.
d.rectangle((0, 0, W*S - 1, H*S - 1), outline=(70, 70, 70), width=S)
img.resize((W, H), Image.LANCZOS).save(out, optimize=True)
