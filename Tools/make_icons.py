#!/usr/bin/env python3
"""Makes the platform icons from EmpireEngineIcon.png (the crown, on a transparent background). Run it after changing the art:

    python3 Tools/make_icons.py

Writes, next to EmpireEngineIcon.png:
    EmpireEngineIcon.ico       Windows: the .exe icons, and the hub's window icons. Transparent, 16 to 256 pixels.
    Engine.Brutal/Ui/Logos/EmpireEngineCrown.png
                         The crown trimmed to its outline, 512 pixels wide, for the splash screen every game opens with.
    EmpireEngineIcon-mac.png   macOS Dock: the crown on a white rounded square laid out on Apple's icon grid (an 824 pixel
                         tile with a 100 pixel margin on a 1024 canvas, and a soft shadow under it), so it looks like
                         the other apps in the Dock.
Needs Pillow (pip install Pillow).
"""
import math
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "EmpireEngineIcon.png"

CANVAS = 1024
TILE = 824              # Apple's icon grid: the tile, centered, leaves 100 pixels on each side
SQUIRCLE_POWER = 5.0    # the tile's continuous corners, as a superellipse |x|^n + |y|^n = 1
ART_SHARE = 0.70        # the crown's longest side as a share of the tile
SUPERSAMPLE = 4         # shapes are drawn this much larger, then scaled down, for smooth edges


def squircle_mask(size, power):
    """A white superellipse filling size x size on black, with antialiased edges."""
    big = size * SUPERSAMPLE
    half = big / 2
    points = []
    steps = 2048
    for i in range(steps):
        t = i / steps * 6.283185307179586
        c, s = math.cos(t), math.sin(t)
        x = half + half * math.copysign(abs(c) ** (2 / power), c)
        y = half + half * math.copysign(abs(s) ** (2 / power), s)
        points.append((x, y))
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).polygon(points, fill=255)
    return mask.resize((size, size), Image.LANCZOS)


def mac_icon(art):
    offset = (CANVAS - TILE) // 2
    tile_mask = Image.new("L", (CANVAS, CANVAS), 0)
    tile_mask.paste(squircle_mask(TILE, SQUIRCLE_POWER), (offset, offset))

    # A soft shadow under the tile, as macOS draws under its own icons: slightly below, blurred, faint.
    shadow_alpha = tile_mask.filter(ImageFilter.GaussianBlur(14)).point(lambda a: int(a * 0.28))
    shadow_alpha = ImageChops.offset(shadow_alpha, 0, 10)
    icon = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    icon.paste(Image.new("RGBA", icon.size, (0, 0, 0, 255)), mask=shadow_alpha)

    icon.paste(Image.new("RGBA", icon.size, (255, 255, 255, 255)), mask=tile_mask)

    # The crown, trimmed to its own outline and centered on the tile.
    crown = art.crop(art.getchannel("A").getbbox())
    scale = TILE * ART_SHARE / max(crown.size)
    crown = crown.resize((round(crown.width * scale), round(crown.height * scale)), Image.LANCZOS)
    icon.alpha_composite(crown, ((CANVAS - crown.width) // 2, (CANVAS - crown.height) // 2))
    return icon


def main():
    art = Image.open(SOURCE).convert("RGBA")
    sizes = [(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
    art.save(ROOT / "EmpireEngineIcon.ico", format="ICO", sizes=sizes)
    mac_icon(art).save(ROOT / "EmpireEngineIcon-mac.png", optimize=True)
    crown = art.crop(art.getchannel("A").getbbox())
    crown.resize((512, round(crown.height * 512 / crown.width)), Image.LANCZOS).save(
        ROOT / "Engine.Brutal" / "Ui" / "Logos" / "EmpireEngineCrown.png", optimize=True)
    print("Wrote EmpireEngineIcon.ico, EmpireEngineIcon-mac.png and Engine.Brutal/Ui/Logos/EmpireEngineCrown.png")


if __name__ == "__main__":
    main()
