# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC

from __future__ import annotations

import os
import tempfile
from dataclasses import dataclass, field

import bpy
from bpy.props import BoolProperty, StringProperty
from bpy_extras.io_utils import ImportHelper
from mathutils import Vector

from .core import VERSION
from .core.anchor import ANCHOR_KEY, BUNDLE_KEY, Placement, bundle_record, decide_placement
from .core.bundle import BundleRefused, Plan, plan_import, scene_properties, wrapper_stage
from .core.feedback import feedback_url
from .core.source import left_behind_note, open_bundle, remove_tree

ROOT_LAYER = "Site.usda"


@dataclass
class ImportResult:
    plan: Plan
    objects: list = field(default_factory=list)
    notes: list = field(default_factory=list)
    placement: Placement | None = None


def import_bundle(context: bpy.types.Context, path: str, include_features: bool = True) -> ImportResult:
    scratch = tempfile.mkdtemp(prefix="mantleplace-")
    try:
        with open_bundle(path) as bundle:
            plan = plan_import(bundle.manifest, include_features)
            notes = list(plan.notes)
            context_file = _take(bundle, plan, plan.context_path, scratch, notes)
            features_file = None
            if plan.features_path is not None:
                features_file = _take(bundle, plan, plan.features_path, scratch, notes)

        root = os.path.join(scratch, ROOT_LAYER)
        with open(root, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(wrapper_stage(context_file, features_file))

        before = set(bpy.data.objects)
        before_collections = set(bpy.data.collections)
        outcome = bpy.ops.wm.usd_import(
            filepath=root,
            import_textures_mode="IMPORT_PACK",
            create_collection=True,
            set_frame_range=False,
            scale=1.0,
        )
        if "FINISHED" not in outcome:
            raise BundleRefused("Blender's USD import did not finish; see the system console.")
        result = ImportResult(plan, [o for o in bpy.data.objects if o not in before], notes)
        collections = [c for c in bpy.data.collections if c not in before_collections]
    finally:
        leftover = remove_tree(scratch)

    if leftover:
        result.notes.append(left_behind_note(leftover))

    result.placement = _place(context.scene, plan, result.objects, collections)
    if result.placement.note:
        result.notes.append(result.placement.note)
    return result


def _take(bundle, plan: Plan, archive_path: str, scratch: str, notes: list) -> str:
    local, verified = bundle.materialise(archive_path, scratch, plan.digests.get(archive_path))
    if not verified:
        notes.append(f"{archive_path} has no sha256 in its manifest, so it was imported without "
                     "an integrity check.")
    return local


def _place(scene: bpy.types.Scene, plan: Plan, objects: list, collections: list) -> Placement:
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    georeference = plan.georeference
    placement = decide_placement(_scene_anchor(scene), georeference)
    if georeference is None:
        return placement
    if placement.anchor:
        for key, value in scene_properties(georeference).items():
            scene[key] = value

    created = set(objects)
    roots = [o for o in objects if o.parent not in created]
    if any(placement.offset):
        offset = Vector(placement.offset)
        for obj in roots:
            obj.location += offset
        bpy.context.view_layer.update()

    record = bundle_record(georeference, placement)
    nested = {child for collection in collections for child in collection.children}
    for datablock in [*(c for c in collections if c not in nested), *roots]:
        datablock[BUNDLE_KEY] = record
    return placement


def _scene_anchor(scene: bpy.types.Scene) -> object:
    value = scene.get(ANCHOR_KEY)
    return value.to_dict() if hasattr(value, "to_dict") else value


class MANTLEPLACE_OT_import_bundle(bpy.types.Operator, ImportHelper):
    bl_idname = "import_scene.mantleplace_bundle"
    bl_label = "Import Bundle"
    bl_options = {"REGISTER", "UNDO"}

    filename_ext = ".zip"
    filter_glob: StringProperty(default="*.zip;*.json", options={"HIDDEN"})
    include_features: BoolProperty(
        name="Features",
        description="Compose the bundle's vector features (Features.usdz) over the site",
        default=True,
    )

    def execute(self, context):
        try:
            result = import_bundle(context, self.filepath, self.include_features)
        except BundleRefused as exc:
            self.report({"ERROR"}, str(exc))
            return {"CANCELLED"}
        for note in result.notes:
            self.report({"WARNING"}, note)
        placement = result.placement
        if result.plan.georeference is None:
            placed = "not placed"
        elif placement.anchor:
            placed = "placed at its site origin"
        elif any(placement.offset):
            east, north, up = placement.offset
            placed = f"placed beside the first bundle ({east:.3f}, {north:.3f}, {up:.3f} m)"
        elif placement.note:
            placed = "left at its own offsets"
        else:
            placed = "placed at the first bundle's site origin"
        self.report({"INFO"}, f"Imported {len(result.objects)} objects, {placed}.")
        return {"FINISHED"}


class MANTLEPLACE_OT_send_feedback(bpy.types.Operator):
    bl_idname = "mantleplace.send_feedback"
    bl_label = "Send Feedback"

    def execute(self, context):
        blender_version = ".".join(str(part) for part in bpy.app.version)
        bpy.ops.wm.url_open(url=feedback_url(VERSION, blender_version))
        return {"FINISHED"}


class MANTLEPLACE_AP_preferences(bpy.types.AddonPreferences):
    bl_idname = __package__

    def draw(self, context):
        self.layout.operator(MANTLEPLACE_OT_send_feedback.bl_idname, icon="URL")


def _import_menu(self, context):
    self.layout.operator(MANTLEPLACE_OT_import_bundle.bl_idname, text="Mantle Place Bundle (.zip)")


def _help_menu(self, context):
    self.layout.separator()
    self.layout.operator(MANTLEPLACE_OT_send_feedback.bl_idname, icon="URL")


_CLASSES = (MANTLEPLACE_OT_import_bundle, MANTLEPLACE_OT_send_feedback, MANTLEPLACE_AP_preferences)


def register() -> None:
    for cls in _CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.TOPBAR_MT_file_import.append(_import_menu)
    bpy.types.TOPBAR_MT_help.append(_help_menu)


def unregister() -> None:
    bpy.types.TOPBAR_MT_help.remove(_help_menu)
    bpy.types.TOPBAR_MT_file_import.remove(_import_menu)
    for cls in reversed(_CLASSES):
        bpy.utils.unregister_class(cls)
