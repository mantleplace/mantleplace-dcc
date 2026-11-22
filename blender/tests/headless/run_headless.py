# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC

import json
import os
import sys
import tempfile
import time
import zipfile

import bpy
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(os.path.dirname(HERE)))

import mantleplace_blender  # noqa: E402
from mantleplace_blender.core.bundle import plan_import  # noqa: E402
from mantleplace_blender.core.source import open_bundle  # noqa: E402

OFFSET_BOUND_M = 50_000.0


def check(condition, message):
    print(("ok    " if condition else "FAIL  ") + message, flush=True)
    if not condition:
        raise AssertionError(message)


def bundle_argument():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if len(args) != 1:
        raise SystemExit("usage: blender --background --python run_headless.py -- <bundle>")
    return os.path.abspath(args[0])


def fresh_scene():
    bpy.ops.wm.read_homefile(use_empty=True)


def run_import(path, include_features):
    started = time.monotonic()
    outcome = bpy.ops.import_scene.mantleplace_bundle(filepath=path, include_features=include_features)
    print(f"      import took {time.monotonic() - started:.1f} s", flush=True)
    return outcome


def curve_objects():
    return [o for o in bpy.data.objects if o.type in {"CURVE", "CURVES"}]


def world_extent(objects):
    far = 0.0
    for obj in objects:
        for corner in obj.bound_box:
            world = obj.matrix_world @ Vector(corner)
            far = max(far, abs(world.x), abs(world.y))
    return far


def main():
    bundle = bundle_argument()
    print(f"Blender {bpy.app.version_string}, bundle {os.path.basename(bundle)}", flush=True)
    with open_bundle(bundle) as opened:
        manifest = opened.manifest
    plan = plan_import(manifest)
    check(plan.features_path is not None, f"the bundle carries a Features file ({plan.features_path})")
    for note in plan.notes:
        print(f"      note: {note}", flush=True)

    mantleplace_blender.register()
    check("mantleplace_bundle" in dir(bpy.ops.import_scene), "the plugin registers its operator")

    fresh_scene()
    check(run_import(bundle, True) == {"FINISHED"}, "the import finishes with Features")
    scene = bpy.context.scene
    names = {o.name for o in bpy.data.objects}
    check("Terrain" in names, "the Context file's Terrain mesh is in the scene")
    check(any(o.type == "MESH" for o in bpy.data.objects if o.name != "Terrain"),
          "more site meshes than the terrain came in")
    with_features = len(curve_objects())
    check(with_features > 0, f"the Features file's curves came in ({with_features} curve objects)")
    check("Site" in bpy.data.collections, "the site is one collection, named Site")

    projected = manifest["location"]["site_origin"]["projected"]
    origin = scene.get("mantleplace_site_origin")
    check(origin is not None, "the scene records the site origin")
    check((origin["x"], origin["y"], origin["z"]) == (projected["x"], projected["y"], projected["z"]),
          f"the recorded origin is the manifest's, verbatim ({projected['x']}, {projected['y']}, "
          f"{projected['z']})")
    check(origin["crs"] == projected["crs"]["code"], f"the origin's CRS is {projected['crs']['code']}")
    if projected["unit"] == "m":
        check(scene.get("SRID") == projected["crs"]["code"]
              and scene.get("crs x") == projected["x"] and scene.get("crs y") == projected["y"],
              "the GIS scene-origin keys name the same point")
    check(scene.unit_settings.system == "METRIC" and scene.unit_settings.scale_length == 1.0,
          "the scene is in metres")

    meshes = [o for o in bpy.data.objects if o.type == "MESH"]
    extent = world_extent(meshes)
    check(extent < OFFSET_BOUND_M,
          f"the geometry sits at its offsets from the site origin (farthest {extent:.0f} m)")

    images = [i for i in bpy.data.images if i.source == "FILE"]
    check(images and all(i.packed_file is not None for i in images),
          f"every texture is packed into the .blend ({len(images)} images)")

    fresh_scene()
    check(run_import(bundle, False) == {"FINISHED"}, "the import finishes without Features")
    check(len(curve_objects()) == 0, "no Features curves come in when Features is left out")
    check("Terrain" in {o.name for o in bpy.data.objects}, "the Terrain still comes in alone")

    fresh_scene()
    with tempfile.TemporaryDirectory() as scratch:
        ahead = os.path.join(scratch, "ahead.zip")
        with zipfile.ZipFile(ahead, "w") as archive:
            archive.writestr("Metadata/manifest.json", json.dumps({**manifest, "version": "3.0.0"}))
        try:
            refused = run_import(ahead, True) == {"CANCELLED"}
        except RuntimeError as exc:
            refused = "3.0.0" in str(exc)
        check(refused and not bpy.data.objects, "an MPB 3.0.0 bundle is refused and imports nothing")

    mantleplace_blender.unregister()
    print("PASS", flush=True)


main()
