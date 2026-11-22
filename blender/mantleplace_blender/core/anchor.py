# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC

from __future__ import annotations

import math
from dataclasses import dataclass

from .bundle import ANCHOR_KEY, ORIGIN_UNITS, Georeference, scene_properties

BUNDLE_KEY = "mantleplace_bundle"

MAX_OFFSET_M = 16384.0

_NO_OFFSET = (0.0, 0.0, 0.0)


@dataclass(frozen=True)
class Placement:
    anchor: bool
    offset: tuple
    note: str | None


def decide_placement(anchor: object, georeference: Georeference | None) -> Placement:
    if georeference is None:
        return Placement(False, _NO_OFFSET, None)
    if anchor is None:
        return Placement(True, _NO_OFFSET, None)

    problem = _anchor_problem(anchor)
    if problem is None:
        problem = _crs_problem(anchor, georeference)
    if problem is None and anchor["z_reference"] != georeference.z_reference:
        problem = (f"it measures height from {georeference.z_reference!r} and the anchor from "
                   f"{anchor['z_reference']!r}, and relating them would take a datum shift, which "
                   "this plugin never does")
    if problem is not None:
        return _stays(problem)

    offset = _offset(anchor, georeference)
    if not all(math.isfinite(part) for part in offset):
        return _stays("its site origin or the anchor's is not a finite number")
    farthest = max(abs(part) for part in offset)
    if farthest > MAX_OFFSET_M:
        return _stays(f"it is {farthest:.0f} metres from the anchor on one axis, beyond the "
                      f"{MAX_OFFSET_M:.0f} metres Blender's 32-bit object positions hold to a "
                      "millimetre")
    return Placement(False, offset, None)


def bundle_record(georeference: Georeference, placement: Placement) -> dict:
    return {
        "site_origin": scene_properties(georeference)[ANCHOR_KEY],
        "offset_m": [float(part) for part in placement.offset],
        "is_anchor": placement.anchor,
    }


def _stays(reason: str) -> Placement:
    return Placement(False, _NO_OFFSET,
                     f"Left at its own offsets, not beside the scene's first bundle: {reason}.")


def _number(value: object) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _anchor_problem(anchor: object) -> str | None:
    if not isinstance(anchor, dict):
        return "the scene's anchor record is not readable"
    if not isinstance(anchor.get("crs"), str):
        return "the scene's anchor record names no CRS"
    if not all(_number(anchor.get(axis)) for axis in ("x", "y", "z")):
        return "the scene's anchor record has no numeric x, y and z"
    if anchor.get("unit") not in ORIGIN_UNITS:
        return f"the scene's anchor record has a unit {anchor.get('unit')!r} this plugin does not read"
    if not isinstance(anchor.get("z_reference"), str):
        return "the scene's anchor record names no z_reference"
    return None


def _crs_problem(anchor: dict, georeference: Georeference) -> str | None:
    code = georeference.crs_code
    if code is None or anchor["crs"] == "a CRS stated without a code":
        return ("a CRS stated without a code cannot be compared with the anchor's, and relating "
                "them would take a reprojection, which this plugin never does")
    if code.strip().upper() != anchor["crs"].strip().upper():
        return (f"its site origin is in {code} and the anchor is in {anchor['crs']}, and relating "
                "them would take a reprojection, which this plugin never does")
    return None


def _offset(anchor: dict, georeference: Georeference) -> tuple:
    later = ORIGIN_UNITS[georeference.unit]
    first = ORIGIN_UNITS[anchor["unit"]]
    sums = []
    for axis in ("x", "y", "z"):
        a, b = float(anchor[axis]), float(getattr(georeference, axis))
        sums.append((b - a) * later if later == first else b * later - a * first)
    return tuple(sums)
