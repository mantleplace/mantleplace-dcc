from typing import NamedTuple, Optional, Sequence, Tuple

MATTE_FLOOR = 40


def matte_value(min_channel: int) -> int:
    if min_channel <= MATTE_FLOOR:
        return 0
    span = 255 - MATTE_FLOOR
    return _clamp_byte(_round_half_up((min_channel - MATTE_FLOOR) * 255 / span))


def matte_lut() -> bytes:
    return _lut(matte_value)


def bounding_box(
    alpha: Sequence[int], width: int, threshold: int = 128
) -> Optional[Tuple[int, int, int, int]]:
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


MARGIN = 0.12

HOST_SIZES = (16, 24, 32, 48, 64)


class Placement(NamedTuple):
    width: int
    height: int
    left: int
    top: int


def place(src_width: int, src_height: int, size: int, margin: float = MARGIN) -> Placement:
    inner = size * (1 - 2 * margin)
    scale = min(inner / src_width, inner / src_height)
    width = max(1, _round_half_up(src_width * scale))
    height = max(1, _round_half_up(src_height * scale))
    return Placement(width, height, (size - width) // 2, (size - height) // 2)


SNAP_GAIN = 2.6
SNAP_PIVOT = 128

SNAP_MAX_SIZE = 24


def snap_value(alpha: int, gain: float = SNAP_GAIN, pivot: int = SNAP_PIVOT) -> int:
    return _clamp_byte(_round_half_up((alpha - pivot) * gain + pivot))


def snap_lut() -> bytes:
    return _lut(snap_value)


def uses_snapping(size: int) -> bool:
    return size <= SNAP_MAX_SIZE


def _lut(rule) -> bytes:
    return bytes(rule(value) for value in range(256))


def _round_half_up(value: float) -> int:
    return int(value + 0.5) if value >= 0 else -int(-value + 0.5)


def _clamp_byte(value: int) -> int:
    return 0 if value < 0 else 255 if value > 255 else value
