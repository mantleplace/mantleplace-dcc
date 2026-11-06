# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""Where a later bundle sits against the one already in the scene. Pure: no Blender, no I/O.

Every bundle stores its geometry as offsets from its own site origin (spec/format.md §6.8), so two
bundles imported into one scene would both sit about the scene origin. The first bundle whose site
origin is applied becomes the scene's **anchor** and is never replaced. A later bundle is moved by
the difference of the two projected points, which is the arithmetic §6.8 gives a reader for an
offset file (the file's offsets added to `projected`), taken relative to the anchor so the scene
stays near its origin.

Nothing here derives a placement value. It subtracts two published points, and only when they are
in the same CRS and measure height from the same `z_reference`; in any other case the bundle stays
at its own offsets and the curator is told why, because relating the two would take a
reprojection or a datum shift, which this plugin never does.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

from .bundle import ANCHOR_KEY, ORIGIN_UNITS, Georeference, scene_properties

#: The custom property on a bundle's collection and top-level objects: its own origin and offset.
BUNDLE_KEY = "mantleplace_bundle"

#: The farthest a bundle is moved from the anchor on any one axis, in metres.
#:
#: Blender stores an object's location as a 32-bit float, which has 24 significant bits. Between
#: 2**13 and 2**14 metres the spacing of representable values is 2**-10 m, just under a
#: millimetre, and at 2**14 = 16384 m it is a millimetre. Beyond that the spacing doubles for
#: every doubling of the distance, so a seam between two tiles would open by more than a
#: millimetre and the viewport's own depth precision would degrade with it. A tile order is a few
#: kilometres across; 16.4 km from the anchor keeps millimetre fidelity and is far beyond a
#: realistic order of adjacent bundles.
MAX_OFFSET_M = 16384.0

_NO_OFFSET = (0.0, 0.0, 0.0)


@dataclass(frozen=True)
class Placement:
    """What one import does about the scene's anchor."""

    #: True when this bundle becomes the scene's anchor (the scene keys are written).
    anchor: bool
    #: Metres to move the bundle's imported objects by, east, north, up.
    offset: tuple
    #: One sentence for the curator where the bundle could not be moved, else None.
    note: str | None


def decide_placement(anchor: object, georeference: Georeference | None) -> Placement:
    """Decide the anchor and the offset for a bundle whose site origin is `georeference`.

    `anchor` is the `mantleplace_site_origin` record the scene holds (as `scene_properties`
    writes it), or None where no bundle has been anchored yet. A bundle with no applied origin
    neither anchors nor moves; `plan_import` has already said why, so no second note is added.
    """
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
    """The record a bundle keeps of where it came from: its own origin and how far it was moved."""
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
    """Why the scene's record cannot be an anchor, or None. It is a custom property: untrusted."""
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
    """The later bundle's projected point minus the anchor's, in metres, each read in its own unit."""
    later = ORIGIN_UNITS[georeference.unit]
    first = ORIGIN_UNITS[anchor["unit"]]
    sums = []
    for axis in ("x", "y", "z"):
        a, b = float(anchor[axis]), float(getattr(georeference, axis))
        # Subtract before scaling when the units agree: the difference is then exact in the CRS's
        # own unit, and a seam is never opened by rounding two large coordinates apart.
        sums.append((b - a) * later if later == first else b * later - a * first)
    return tuple(sums)
