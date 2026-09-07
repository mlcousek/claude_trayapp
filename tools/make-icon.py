"""Draws the app icon: a terracotta ring arc on a dark rounded tile, the same language as the tray icon.

Writes src/ClaudeTrayApp/Assets/app.ico (16 to 256 px) and docs/screenshots/app-icon.png (256 px).
Needs Pillow (`pip install pillow`); run from the repository root: `python tools/make-icon.py`.
The icon is this project's own; it carries no Anthropic logo or trademark.
"""

import math
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
MASTER = 1024

SURFACE = (31, 30, 29, 255)      # #1F1E1D
HAIRLINE = (58, 57, 55, 255)     # #3A3937
TRACK = (74, 72, 69, 255)        # a shade above the hairline so the track reads on the tile
ACCENT = (217, 119, 87, 255)     # #D97757, the Claude terracotta used for the Ok state
ARC_FRACTION = 0.72              # how much of the ring the arc covers


def draw_master(size: int) -> Image.Image:
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    inset = size * 0.04
    radius = size * 0.22
    draw.rounded_rectangle(
        (inset, inset, size - inset, size - inset),
        radius=radius,
        fill=SURFACE,
        outline=HAIRLINE,
        width=max(1, size // 128),
    )

    centre = size / 2
    ring_radius = size * 0.30
    thickness = size * 0.115
    box = (centre - ring_radius, centre - ring_radius, centre + ring_radius, centre + ring_radius)

    # PIL draws the arc width inwards from the bounding box, so the centreline sits at ring_radius - thickness / 2.
    draw.arc(box, start=0, end=360, fill=TRACK, width=int(thickness))
    start = -90
    end = start + 360 * ARC_FRACTION
    draw.arc(box, start=start, end=end, fill=ACCENT, width=int(thickness))

    centreline = ring_radius - thickness / 2
    cap = thickness / 2
    for angle in (start, end):
        rad = math.radians(angle)
        cx = centre + centreline * math.cos(rad)
        cy = centre + centreline * math.sin(rad)
        draw.ellipse((cx - cap, cy - cap, cx + cap, cy + cap), fill=ACCENT)

    return image


def main() -> None:
    master = draw_master(MASTER)
    frames = [master.resize((s, s), Image.LANCZOS) for s in SIZES]

    assets = ROOT / "src" / "ClaudeTrayApp" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)
    ico = assets / "app.ico"
    largest = frames[-1]
    largest.save(ico, format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])

    preview = ROOT / "docs" / "screenshots" / "app-icon.png"
    preview.parent.mkdir(parents=True, exist_ok=True)
    largest.save(preview, format="PNG")
    print(f"wrote {ico} ({ico.stat().st_size} bytes) and {preview}")


if __name__ == "__main__":
    main()
