#!/usr/bin/env python3

import argparse
import sys
from pathlib import Path

import mark_geometry as geometry

try:
    from PIL import Image, ImageChops, ImageDraw
except ImportError:  # pragma: no cover
    sys.exit(
        "Pillow is required to render the mark, and is the only dependency this tool has.\n"
        "  python -m pip install pillow\n"
        "The geometry tests beside this script need no dependency at all."
    )

SUPERSAMPLE = 16

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent


def _ellipse(draw, circle, scale):
    cx, cy, r = circle
    draw.ellipse(((cx - r) * scale, (cy - r) * scale, (cx + r) * scale, (cy + r) * scale), fill=255)


def _coverage(layer, side, scale):
    mask = Image.new("L", (side, side), 0)
    if layer.kind == "circle":
        _ellipse(ImageDraw.Draw(mask), layer.circle, scale)
        return mask
    for polygon in layer.polygons:
        ring = Image.new("L", (side, side), 0)
        ImageDraw.Draw(ring).polygon([(x * scale, y * scale) for x, y in polygon], fill=255)
        mask = ImageChops.logical_xor(mask.convert("1"), ring.convert("1")).convert("L")
    return mask


def render(mark: geometry.Mark, size: int) -> Image.Image:
    side = size * SUPERSAMPLE
    scale = side / mark.view[0]

    clip = Image.new("L", (side, side), 0)
    _ellipse(ImageDraw.Draw(clip), mark.clip, scale)

    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    for layer in mark.layers:
        coverage = ImageChops.multiply(_coverage(layer, side, scale), clip)
        paint = Image.new("RGBA", (side, side), layer.fill + (255,))
        paint.putalpha(coverage)
        canvas.alpha_composite(paint)

    return canvas.convert("RGBa").reduce(SUPERSAMPLE).convert("RGBA")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Render the Mantle Place mark into the sizes every host plugin ships.",
    )
    parser.add_argument(
        "--source",
        type=Path,
        default=HERE / geometry.SOURCE_NAME,
        metavar="SVG",
        help=f"the mark's vector source (default: {geometry.SOURCE_NAME} beside this script)",
    )
    parser.add_argument(
        "--out",
        type=Path,
        default=ROOT,
        metavar="DIR",
        help="the repository root to write into (default: this repository)",
    )
    args = parser.parse_args(argv)

    mark = geometry.read_mark(args.source.read_text(encoding="utf-8"))

    total = 0
    for target in geometry.targets():
        path = args.out / target.path
        path.parent.mkdir(parents=True, exist_ok=True)
        render(mark, target.size).save(path, "PNG", optimize=True)
        written = path.stat().st_size
        total += written
        print(f"{target.path:<60} {written:>7,} bytes")
    print(f"{'total':<60} {total:>7,} bytes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
