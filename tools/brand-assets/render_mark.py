#!/usr/bin/env python3
"""Render the Mantle Place mark into the sizes each host ships.

This is the impure half of the triad: it reads a PNG, writes PNGs, and defers every decision
about what those PNGs look like to `mark_geometry.py`, which is tested. Nothing here chooses a
margin, a size or a treatment.

    python tools/brand-assets/render_mark.py --mark-source <path to the canonical master>

The master is a private file and is deliberately not defaulted -- see README.md. This script
therefore cannot run in CI, and no workflow attempts it; `ci-brand-assets` runs the geometry
tests alone.

Pillow is the one dependency, and it is confined to this file:

    python -m pip install pillow
"""

import argparse
from pathlib import Path
from typing import Callable, NamedTuple
import sys

import mark_geometry as geometry

try:
    from PIL import Image, ImageChops
except ImportError:  # pragma: no cover - the message is the whole point
    sys.exit(
        "Pillow is required to render the mark, and is the only dependency this tool has.\n"
        "  python -m pip install pillow\n"
        "The geometry tests beside this script need no dependency at all."
    )

# The tile colour, as MantlePlacePalette::Mantle() already states it for the Unreal host. Read
# from the master rather than trusted blindly -- this value is the assertion, not the source.
MANTLE_ORANGE = (0xFF, 0x71, 0x10)

# Supersampling factor used before the hardened downsample at small sizes. Eight is past the
# point where more changes the result, and keeps a 16 px render honest about the master's curves.
SUPERSAMPLE = 8

REVIT_RESOURCES = Path("revit/src/MantlePlace.Revit.Addin/Resources")
UNREAL_RESOURCES = Path("unreal/MantlePlace/Resources")

# The size Unreal's tab icon and header logo are rendered from. Slate draws the tab at 16 px and
# the vault header at 32 px, so one 64 px source covers both with headroom for a high-DPI editor
# and downsamples cleanly from a power of two. It lands byte-identical to Revit's 64 px render --
# same artwork, same size -- and that is fine: the hosts share no files, only this script.
# ADR 0009 records why the two Unreal brushes point at one file.
UNREAL_SMALL_SIZE = 64

# Unreal's plugin-browser icon, shown large enough that the extrude reads as a shadow rather than
# as a smudge. It is the one asset in either host rendered from the mark exactly as drawn.
UNREAL_ICON_SIZE = 128


def load_master(path: Path) -> Image.Image:
    """Open the canonical master and check it is the artwork this script knows how to cut up."""
    if not path.is_file():
        raise SystemExit(f"No such file: {path}")

    image = Image.open(path).convert("RGB")
    width, height = image.size
    if width != height:
        raise SystemExit(f"The master must be square; {path.name} is {width}x{height}.")
    if width < UNREAL_ICON_SIZE * 4:
        raise SystemExit(
            f"The master is {width} px, which is too small to render from. Use the largest "
            f"available render of the canonical mark (at least {UNREAL_ICON_SIZE * 4} px)."
        )

    corner = image.getpixel((0, 0))
    if corner != MANTLE_ORANGE:
        raise SystemExit(
            f"Expected a full-bleed tile in the brand orange {MANTLE_ORANGE}, but {path.name} "
            f"starts with {corner}. This script renders the square mark, not the wordmark or "
            f"the retired roundel."
        )
    return image


def cut_monogram(master: Image.Image) -> Image.Image:
    """The monogram alone, as an alpha channel cropped to its optical box.

    Cut once and reused for every size: finding the box means scanning sixteen million pixels,
    and the answer does not depend on what it is about to be scaled to.
    """
    red, green, blue = master.split()
    darkest = ImageChops.darker(ImageChops.darker(red, green), blue)
    alpha = darkest.point(geometry.matte_lut())

    box = geometry.bounding_box(alpha.tobytes(), alpha.width)
    if box is None:
        raise SystemExit("Found no monogram in the master: every pixel was tile or extrude.")
    return alpha.crop(box)


def render_small(master: Image.Image, monogram: Image.Image, size: int) -> Image.Image:
    """The host treatment: monogram only, recentred on the tile, hardened at small sizes."""
    spot = geometry.place(monogram.width, monogram.height, size)

    if geometry.uses_snapping(size):
        # Downsample in two steps so the hardening acts on a settled edge rather than on one
        # Lanczos is still ringing, then push that edge toward a hard one.
        supersampled = monogram.resize(
            (spot.width * SUPERSAMPLE, spot.height * SUPERSAMPLE), Image.LANCZOS
        )
        scaled = supersampled.resize((spot.width, spot.height), Image.BOX)
        scaled = scaled.point(geometry.snap_lut())
    else:
        scaled = monogram.resize((spot.width, spot.height), Image.LANCZOS)

    tile = Image.new("RGBA", (size, size), MANTLE_ORANGE + (255,))
    white = Image.new("RGBA", scaled.size, (255, 255, 255, 0))
    white.putalpha(scaled)
    tile.alpha_composite(white, (spot.left, spot.top))
    return tile


def render_verbatim(master: Image.Image, monogram: Image.Image, size: int) -> Image.Image:
    """The mark exactly as drawn, extrude and composition intact.

    Takes the cut monogram it does not use, so that both renderers share one signature and the
    target table below stays a table rather than a branch.
    """
    return master.resize((size, size), Image.LANCZOS).convert("RGBA")


class Target(NamedTuple):
    """One file this script writes, and everything that decides its contents."""

    path: Path
    size: int
    render: Callable[[Image.Image, Image.Image, int], Image.Image]


def targets(root: Path) -> list:
    """Every file this script writes. The single place a size, a path and a treatment meet."""
    plan = [
        Target(root / REVIT_RESOURCES / f"MantlePlaceMark_{size}.png", size, render_small)
        for size in geometry.HOST_SIZES
    ]
    plan.append(
        Target(root / UNREAL_RESOURCES / "MantlePlaceTabIcon.png", UNREAL_SMALL_SIZE, render_small)
    )
    plan.append(
        Target(root / UNREAL_RESOURCES / "Icon128.png", UNREAL_ICON_SIZE, render_verbatim)
    )
    return plan


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Render the Mantle Place mark into the sizes the Revit and Unreal hosts ship.",
        epilog="The master is private and has no default; see tools/brand-assets/README.md.",
    )
    parser.add_argument(
        "--mark-source",
        required=True,
        type=Path,
        metavar="PATH",
        help="the canonical square mark, as the largest available PNG render of it",
    )
    args = parser.parse_args(argv)

    master = load_master(args.mark_source)
    monogram = cut_monogram(master)
    root = Path(__file__).resolve().parent.parent.parent

    total = 0
    for target in targets(root):
        target.path.parent.mkdir(parents=True, exist_ok=True)
        target.render(master, monogram, target.size).save(target.path, "PNG", optimize=True)
        written = target.path.stat().st_size
        total += written
        print(f"{target.path.relative_to(root).as_posix():<60} {written:>7,} bytes")
    print(f"{'total':<60} {total:>7,} bytes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
