"""The decisions behind the host renders of the Mantle Place mark, with no image library in sight.

This is the pure half of the triad ([CONTRIBUTING.md](../../CONTRIBUTING.md#style-and-shape)):
every rule that decides what a rendered mark looks like lives here, on plain numbers and plain
buffers, so `test_mark_geometry.py` can assert it with the standard library alone. The Pillow
half is `render_mark.py`, which reads and writes PNGs and does nothing else.

The two lookup-table functions exist for that split. Applying a per-pixel rule across a 4096 px
master in a Python loop is sixteen million round trips; handing the same rule to the imaging
library as a 256-entry table is one. The rule stays here, and stays tested, either way.

Why any of this is needed at all, rather than scaling the mark down as drawn, is ADR 0009.
"""

from typing import NamedTuple, Optional, Sequence, Tuple

# ---------------------------------------------------------------------------------------------
# Separating the monogram from the tile
# ---------------------------------------------------------------------------------------------

# The canonical mark holds three colours and no others: the tile 0xFF7110, a black extrude and
# the white monogram. Their minimum channels are 16, 0 and 255 -- so a floor set above the
# tile's minimum rejects the tile AND the extrude in a single comparison, and no colour matching
# is needed. 40 sits clear of the tile's 16 with room for the master's antialiasing, and far below
# anything the monogram's own edge reaches.
MATTE_FLOOR = 40


def matte_value(min_channel: int) -> int:
    """Opacity of the monogram at a pixel whose darkest colour channel is `min_channel`.

    Pixels at or below the floor are tile or extrude and vanish; pure white is fully opaque; the
    master's antialiased edges land in between and keep their softness.
    """
    if min_channel <= MATTE_FLOOR:
        return 0
    span = 255 - MATTE_FLOOR
    return _clamp_byte(_round_half_up((min_channel - MATTE_FLOOR) * 255 / span))


def matte_lut() -> bytes:
    """`matte_value` over every possible channel value, for one call into an imaging library."""
    return _lut(matte_value)


# ---------------------------------------------------------------------------------------------
# Finding the monogram's optical box
# ---------------------------------------------------------------------------------------------


def bounding_box(
    alpha: Sequence[int], width: int, threshold: int = 128
) -> Optional[Tuple[int, int, int, int]]:
    """The tightest `(left, top, right, bottom)` holding every pixel at or above `threshold`.

    Right and bottom are exclusive, matching the half-open convention every crop in this repo
    uses. Returns None when nothing reaches the threshold.

    The scan is row-wise through `bytes.translate` and `bytes.find` rather than a per-pixel loop:
    the caller's buffer is sixteen million pixels, and the answer is needed in under a second.
    """
    buffer = bytes(alpha)
    height = len(buffer) // width if width else 0
    if height == 0:
        return None

    mask = bytes(0xFF if value >= threshold else 0x00 for value in range(256))
    flags = buffer.translate(mask)

    left, top, right, bottom = width, -1, 0, -1
    for y in range(height):
        row = flags[y * width : (y + 1) * width]
        first = row.find(0xFF)
        if first == -1:
            continue
        if top == -1:
            top = y
        bottom = y
        left = min(left, first)
        right = max(right, row.rfind(0xFF) + 1)

    if top == -1:
        return None
    return (left, top, right, bottom + 1)


# ---------------------------------------------------------------------------------------------
# Placing the monogram on the tile
# ---------------------------------------------------------------------------------------------

# Every side of the tile keeps this much clear. The canonical mark's own margins are uneven --
# measured off the master, the monogram alone sits at 18.6% left against 14.2% right, and 22.9%
# top against 18.6% bottom -- because the extrude falling to the bottom-right was balancing it.
# Take the extrude away, as every host render does, and that imbalance is simply a lopsided mark,
# so the monogram is recentred on its own optical box rather than inheriting the drift.
MARGIN = 0.12

# Revit's ribbon asks for a 16 px and a 32 px image per button, and Revit scales whatever it is
# given to the display. Supplying the 150% and 200% renders as well means the wiring can hand it
# real pixels instead of an upscale. `Resources/src/README.md` maps display scale to file.
HOST_SIZES = (16, 24, 32, 48, 64)


class Placement(NamedTuple):
    """Where the scaled monogram sits on a square tile, in pixels."""

    width: int
    height: int
    left: int
    top: int


def place(src_width: int, src_height: int, size: int, margin: float = MARGIN) -> Placement:
    """Scale a `src_width` x `src_height` optical box into a `size` tile and centre it.

    Aspect ratio is preserved, the margin is honoured on all four sides, and neither dimension is
    allowed to round away to nothing -- at 16 px a thin box would otherwise scale to zero and
    disappear rather than render badly, which is the harder failure to notice.
    """
    inner = size * (1 - 2 * margin)
    scale = min(inner / src_width, inner / src_height)
    width = max(1, _round_half_up(src_width * scale))
    height = max(1, _round_half_up(src_height * scale))
    return Placement(width, height, (size - width) // 2, (size - height) // 2)


# ---------------------------------------------------------------------------------------------
# Hardening the edge at small sizes
# ---------------------------------------------------------------------------------------------

# Contrast applied to the alpha channel around its midpoint. 2.6 was chosen by eye against the
# rendered set: enough to make a stem solid white at 16 px, not so much that the curve of the `p`
# turns into stairs.
SNAP_GAIN = 2.6
SNAP_PIVOT = 128

# At and below this size the monogram's stems are close to a single pixel, and an antialiased edge
# is then wider than the stroke it is softening -- the mark reads as grey mush rather than as an
# `mp`. Above it, the same hardening is what produces stairs, so it stops.
SNAP_MAX_SIZE = 24


def snap_value(alpha: int, gain: float = SNAP_GAIN, pivot: int = SNAP_PIVOT) -> int:
    """Push an alpha value away from the midpoint, hardening a soft edge toward a hard one."""
    return _clamp_byte(_round_half_up((alpha - pivot) * gain + pivot))


def snap_lut() -> bytes:
    """`snap_value` over every possible alpha value, for one call into an imaging library."""
    return _lut(snap_value)


def uses_snapping(size: int) -> bool:
    """Whether a render at `size` gets the hardened edge."""
    return size <= SNAP_MAX_SIZE


# ---------------------------------------------------------------------------------------------


def _lut(rule) -> bytes:
    """A byte rule as the 256-entry table an imaging library applies in one pass."""
    return bytes(rule(value) for value in range(256))


def _round_half_up(value: float) -> int:
    """Round half away from zero.

    `round` is half-to-even, which makes a placement depend on whether the pixel it landed on
    happened to be odd. These renders are committed binaries; the arithmetic that produces them
    should not have a parity clause in it.
    """
    return int(value + 0.5) if value >= 0 else -int(-value + 0.5)


def _clamp_byte(value: int) -> int:
    return 0 if value < 0 else 255 if value > 255 else value
