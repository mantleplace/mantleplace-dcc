# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""Adjacent bundles in one scene, in Blender with no window: generate, import, measure the seams.

    blender --background --factory-startup --python-exit-code 1 \
        --python blender/tests/headless/run_adjacent.py

The pure suite (`blender/tests/test_anchor.py`) proves the decision; this proves Blender carries it
out. It needs no bundle from anywhere: it writes three small 2.x bundles into a scratch folder, each
a 100 m square of terrain at the centre of its own site origin, ordered side by side, and deletes
them when it ends. Nothing it generates is committed (root CLAUDE.md, Binaries). The square is a
`.usda` layer rather than a `.usdz`, which the manifest pointer allows, so the script needs no USD
library. It prints one line per check, the measured edge offsets in metres, and exits nonzero on the
first failure.
"""

import copy
import hashlib
import json
import os
import sys
import tempfile
import zipfile

import bpy
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(os.path.dirname(HERE)))

import mantleplace_blender  # noqa: E402
from mantleplace_blender import shim  # noqa: E402
from mantleplace_blender.core.bundle import BundleRefused  # noqa: E402

TILE_M = 100.0
#: A measured seam may differ from zero by this much: the 32-bit float spacing at these distances.
TOLERANCE_M = 0.001

FRAME = {
    "horizontal_crs": {"code": "EPSG:32614"},
    "unit_to_metres": 1.0,
    "axis_order": "east-north",
    "axis_mapping": {"x": "east", "y": "north", "z": "up"},
    "coordinate_mode": "offset",
}

LAYER = """#usda 1.0
(
    defaultPrim = "Site"
    metersPerUnit = 1
    upAxis = "Z"
)
def Xform "Site"
{{
    def Mesh "Terrain"
    {{
        int[] faceVertexCounts = [4]
        int[] faceVertexIndices = [0, 1, 2, 3]
        point3f[] points = [(-{h}, -{h}, 0), ({h}, -{h}, 0), ({h}, {h}, 0), (-{h}, {h}, 0)]
    }}
}}
"""


def check(condition, message):
    print(("ok    " if condition else "FAIL  ") + message, flush=True)
    if not condition:
        raise AssertionError(message)


def write_bundle(folder, name, x, y, z=110.672, crs="EPSG:32614", z_reference="ellipsoidal",
                 projected=True):
    """One generated 2.x bundle: a square of terrain whose site origin is (x, y, z) in `crs`."""
    layer = LAYER.format(h=TILE_M / 2).encode("utf-8")
    frame = copy.deepcopy(FRAME)
    frame["horizontal_crs"] = {"code": crs}
    origin = {"latitude": 30.2655, "longitude": -97.752, "ellipsoidal_height_m": 110.672,
              "datum_height_m": 137.792, "vertical_datum": "NAVD88 (GEOID18)",
              "geodetic_crs": {"code": "EPSG:4979"}}
    if projected:
        origin["projected"] = {"crs": {"code": crs}, "x": x, "y": y, "unit": "m", "z": z,
                               "z_reference": z_reference}
    manifest = {
        "version": "2.0.0",
        "layout": {"manifest": "Metadata/manifest.json", "context_usdz": "Site/Context.usda",
                   "features_usdz": None},
        "location": {"site_origin": origin},
        "file_entries": {"Site/Context.usda": {"frame": frame, "odbl_role": "produced-work"}},
        "files": {"Site/Context.usda": {"part": "context_usdz",
                                        "sha256": hashlib.sha256(layer).hexdigest()}},
    }
    path = os.path.join(folder, name + ".zip")
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("Metadata/manifest.json", json.dumps(manifest))
        archive.writestr("Site/Context.usda", layer)
    return path


def fresh_scene():
    bpy.ops.wm.read_homefile(use_empty=True)


def import_one(path):
    """Import through the shim and return the result and the world box of the terrain it added."""
    result = shim.import_bundle(bpy.context, path, include_features=False)
    terrain = [o for o in result.objects if o.type == "MESH"]
    check(len(terrain) == 1, f"{os.path.basename(path)} brought in one terrain mesh")
    corners = [terrain[0].matrix_world @ Vector(c) for c in terrain[0].bound_box]
    box = (min(c.x for c in corners), max(c.x for c in corners),
           min(c.y for c in corners), max(c.y for c in corners),
           min(c.z for c in corners), max(c.z for c in corners))
    return result, box


def main():
    print(f"Blender {bpy.app.version_string}", flush=True)
    mantleplace_blender.register()
    with tempfile.TemporaryDirectory(prefix="mantleplace-adjacent-") as folder:
        east0, north0 = 620050.0, 3348864.5
        a = write_bundle(folder, "a", east0, north0)
        b = write_bundle(folder, "b", east0 + TILE_M, north0)               # east of a
        c = write_bundle(folder, "c", east0, north0 + TILE_M)               # north of a
        other_crs = write_bundle(folder, "crs", east0 + 2 * TILE_M, north0, crs="EPSG:32615")
        other_z = write_bundle(folder, "z", east0 + 2 * TILE_M, north0, z_reference="orthometric")
        no_projected = write_bundle(folder, "np", east0 + 2 * TILE_M, north0, projected=False)
        too_far = write_bundle(folder, "far", east0 + 200_000.0, north0)

        fresh_scene()
        scene = bpy.context.scene
        result_a, box_a = import_one(a)
        check(result_a.placement.anchor and not result_a.notes, "the first bundle is the anchor, no warning")
        anchor = scene["mantleplace_site_origin"].to_dict()
        check(anchor["x"] == east0 and anchor["y"] == north0, "the scene records the first bundle's origin")
        check(abs(box_a[0] + TILE_M / 2) < TOLERANCE_M and abs(box_a[2] + TILE_M / 2) < TOLERANCE_M,
              "the anchor's terrain is at its own offsets about the scene origin")

        result_b, box_b = import_one(b)
        seam_ab = box_b[0] - box_a[1]
        print(f"      measured edge offset, b west edge to a east edge: {seam_ab:.6f} m", flush=True)
        check(abs(seam_ab) < TOLERANCE_M, "b sits edge to edge with a, east of it")
        check(abs(box_b[2] - box_a[2]) < TOLERANCE_M and abs(box_b[3] - box_a[3]) < TOLERANCE_M,
              "b's north and south edges run level with a's")
        check(not result_b.notes and list(result_b.placement.offset) == [TILE_M, 0.0, 0.0],
              "b was moved 100 m east, no warning")

        result_c, box_c = import_one(c)
        seam_ac = box_c[2] - box_a[3]
        seam_bc_x = box_c[1] - box_b[0]
        print(f"      measured edge offset, c south edge to a north edge: {seam_ac:.6f} m", flush=True)
        print(f"      measured corner offset, c east edge to b west edge: {seam_bc_x:.6f} m", flush=True)
        check(abs(seam_ac) < TOLERANCE_M, "c sits edge to edge with a, north of it")
        check(abs(seam_bc_x) < TOLERANCE_M and abs(box_c[2] - box_b[3]) < TOLERANCE_M,
              "c meets b at the corner")

        now = scene["mantleplace_site_origin"].to_dict()
        check(now == anchor, "the scene's anchor record survived the later imports")
        check(scene.get("crs x") == east0 and scene.get("crs y") == north0,
              "the GIS scene-origin keys still name the anchor")
        records = [coll["mantleplace_bundle"].to_dict() for coll in bpy.data.collections
                   if "mantleplace_bundle" in coll]
        check(len(records) == 3, "each bundle's collection keeps its own record")
        check(sorted(r["site_origin"]["x"] for r in records) == [east0, east0, east0 + TILE_M]
              and sum(r["is_anchor"] for r in records) == 1, "the records name three origins, one anchor")

        for label, path in (("another CRS", other_crs), ("another z_reference", other_z),
                            ("no projected block", no_projected), ("beyond the limit", too_far)):
            result, box = import_one(path)
            offsets_kept = abs(box[0] + TILE_M / 2) < TOLERANCE_M and abs(box[2] + TILE_M / 2) < TOLERANCE_M
            warnings = [n for n in result.notes if "origin" in n.lower() or "own offsets" in n]
            print(f"      {label}: {warnings}", flush=True)
            check(offsets_kept, f"{label}: stays at its own offsets")
            check(len(warnings) == 1, f"{label}: exactly one warning")
            check(scene["mantleplace_site_origin"].to_dict() == anchor, f"{label}: the anchor is untouched")

        # A bundle the importer refuses outright leaves the anchor and the objects alone.
        before = len(bpy.data.objects)
        bad = os.path.join(folder, "bad.zip")
        with zipfile.ZipFile(bad, "w") as archive:
            archive.writestr("Metadata/manifest.json", json.dumps({"version": "3.0.0"}))
        try:
            shim.import_bundle(bpy.context, bad, include_features=False)
            refused = False
        except BundleRefused:
            refused = True
        check(refused and len(bpy.data.objects) == before, "a refused bundle changes nothing")

    mantleplace_blender.unregister()
    print("PASS", flush=True)


main()
