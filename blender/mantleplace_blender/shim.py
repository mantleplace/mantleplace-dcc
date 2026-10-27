# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""The host shim: the Blender operators, menus and preferences, and the calls into Blender's API.

Everything decided here comes from the pure core; this file only moves bytes to disk, calls
Blender's own USD import, and writes what the core returns onto the scene. A rule that cannot be
asserted without Blender running is in the wrong file (HPS-02).
"""

from __future__ import annotations

import os
import tempfile
from dataclasses import dataclass, field

import bpy
from bpy.props import BoolProperty, StringProperty
from bpy_extras.io_utils import ImportHelper

from .core import VERSION
from .core.bundle import BundleRefused, Plan, plan_import, scene_properties, wrapper_stage
from .core.feedback import feedback_url
from .core.source import open_bundle, remove_tree

#: The name of the root layer the import opens; Blender names the new collection after it.
ROOT_LAYER = "Site.usda"


@dataclass
class ImportResult:
    plan: Plan
    objects: list = field(default_factory=list)
    notes: list = field(default_factory=list)


def import_bundle(context: bpy.types.Context, path: str, include_features: bool = True) -> ImportResult:
    """Import one bundle into the current scene. Raises `BundleRefused` with the curator's reason."""
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
        # Textures are packed into the .blend, so the scratch folder can go once this returns.
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
    finally:
        remove_tree(scratch)

    _place(context.scene, plan)
    return result


def _take(bundle, plan: Plan, archive_path: str, scratch: str, notes: list) -> str:
    local, verified = bundle.materialise(archive_path, scratch, plan.digests.get(archive_path))
    if not verified:
        notes.append(f"{archive_path} has no sha256 in its manifest, so it was imported without "
                     "an integrity check.")
    return local


def _place(scene: bpy.types.Scene, plan: Plan) -> None:
    """Make the scene's world origin the site origin, in metres, and say so on the scene.

    The geometry is left at its offsets. Blender stores object transforms as 32-bit floats, which
    at a projected northing are coarser than half a metre, which is exactly why the files are
    offsets (spec/format.md §6.8); moving the site to its full coordinates would lose what the
    format kept.
    """
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    if plan.georeference is None:
        return
    for key, value in scene_properties(plan.georeference).items():
        scene[key] = value


class MANTLEPLACE_OT_import_bundle(bpy.types.Operator, ImportHelper):
    """Import a Mantle Place bundle's site, placed at its site origin"""

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
        placed = "placed at its site origin" if result.plan.georeference else "not placed"
        self.report({"INFO"}, f"Imported {len(result.objects)} objects, {placed}.")
        return {"FINISHED"}


class MANTLEPLACE_OT_send_feedback(bpy.types.Operator):
    """Tell Mantle Place about a problem, in your browser"""

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
