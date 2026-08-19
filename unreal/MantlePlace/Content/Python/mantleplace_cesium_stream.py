# Copyright Mantle Place. All Rights Reserved.
"""
Stream a Mantle Place ETL bundle into Cesium for Unreal.

Calls the C++ UMantlePlaceImporterLibrary.stream_bundle_into_cesium(zip) -- which extracts the bundle's
own Cesium-ready quantized-mesh terrain + imagery and hosts them on a local loopback server -- then
spawns an ACesium3DTileset (from the served layer.json URL) and a single-tile imagery raster overlay
under the level's CesiumGeoreference, alongside Cesium World Terrain for apples-to-apples QA.

This is the "stream locally into Unreal via Cesium" capability that complements the native-asset import.
Download-to-own: nothing is streamed from the platform, only from the user's local copy of the bundle.

Usage (Python console, or an Editor Utility Widget "Stream into Cesium" button via Execute Python):
    import mantleplace_cesium_stream as mp
    mp.stream_into_cesium(r"C:/path/to/bundle.zip")

NOTE: the exact Cesium-for-Unreal Python property/enum names below are validated against v2.22.1 during
the editor step; risky calls are wrapped so a name mismatch degrades to a warning rather than aborting.
"""
import unreal

_TILESET_LABEL = "MP_CesiumStream_Terrain"


def _find_georeference():
    """Return the level's CesiumGeoreference (the existing CES_Georeference), or None."""
    for actor in unreal.EditorActorSubsystem().get_all_level_actors():
        if isinstance(actor, unreal.CesiumGeoreference):
            return actor
    return None


def _remove_existing(label):
    eas = unreal.EditorActorSubsystem()
    for actor in eas.get_all_level_actors():
        if actor.get_actor_label() == label:
            eas.destroy_actor(actor)


def _add_raster_overlay(actor, overlay_class, name):
    """Add a UCesiumRasterOverlay component to a level-actor *instance*.

    UE 5.8 does not expose AActor.add_component_by_class to Python, so we use the SubobjectDataSubsystem
    -- the same path the editor's own "Add Component" button uses. Returns the new component, or None on
    failure (the caller degrades to terrain-without-imagery rather than aborting the whole stream).
    """
    sds = unreal.get_engine_subsystem(unreal.SubobjectDataSubsystem)
    if sds is None:
        return None
    SDLib = unreal.SubobjectDataBlueprintFunctionLibrary
    actor_handle = None
    for handle in sds.k2_gather_subobject_data_for_instance(actor):
        if SDLib.is_actor(SDLib.get_data(handle)):
            actor_handle = handle
            break
    if actor_handle is None:
        return None
    new_handle, failure = sds.add_new_subobject(
        unreal.AddNewSubobjectParams(parent_handle=actor_handle, new_class=overlay_class))
    if not failure.is_empty():
        unreal.log_warning("[MantlePlace] add overlay subobject failed: {}".format(failure))
        return None
    sds.rename_subobject(handle=new_handle, new_name=unreal.Text(name))
    return SDLib.get_associated_object(SDLib.get_data(new_handle))


def stream_into_cesium(zip_path):
    """Start the local server (C++) and wire up the Cesium tileset + imagery overlay. Returns the tileset."""
    info = unreal.MantlePlaceImporterLibrary.stream_bundle_into_cesium(zip_path)
    if not info.success:
        unreal.log_error("[MantlePlace] stream failed: {}".format(info.message))
        return None
    unreal.log("[MantlePlace] {}".format(info.message))

    georef = _find_georeference()
    if georef is None:
        unreal.log_warning("[MantlePlace] No CesiumGeoreference in the level; spawning a default one.")
        georef = unreal.EditorActorSubsystem().spawn_actor_from_class(
            unreal.CesiumGeoreference, unreal.Vector(0, 0, 0))

    # Spawn (replace) the bundle's terrain tileset, pointed at the local server's layer.json.
    _remove_existing(_TILESET_LABEL)
    tileset = unreal.EditorActorSubsystem().spawn_actor_from_class(
        unreal.Cesium3DTileset, unreal.Vector(0, 0, 0))
    tileset.set_actor_label(_TILESET_LABEL)
    try:
        tileset.set_editor_property("tileset_source", unreal.TilesetSource.FROM_URL)
    except Exception as exc:
        unreal.log_warning("[MantlePlace] tileset_source set failed ({}); set it to 'From Url' by hand.".format(exc))
    tileset.set_editor_property("url", info.cesium_terrain_url)
    tileset.set_editor_property("georeference", georef)

    # Single-tile imagery raster overlay over the AOI bbox (Geographic / EPSG:4326). The AOI PNG is
    # stretched across the bbox rectangle -- sub-pixel skew over a few-km AOI, fine for visual QA. A
    # single tile (1x1 custom tiling scheme, level 0 only) maps the whole image to the bbox; Cesium
    # downsamples it to MaximumTextureSize (lifted to 4096 for sharper QA than the 2048 default).
    if info.imagery_url and info.has_bbox:
        overlay = _add_raster_overlay(
            tileset, unreal.CesiumUrlTemplateRasterOverlay, "MP_ImageryOverlay")
        if overlay is None:
            unreal.log_warning(
                "[MantlePlace] could not create the imagery overlay; terrain streamed without imagery.")
        else:
            overlay.set_editor_property("template_url", info.imagery_url)
            overlay.set_editor_property("projection", unreal.CesiumUrlTemplateRasterOverlayProjection.GEOGRAPHIC)
            overlay.set_editor_property("specify_tiling_scheme", True)
            overlay.set_editor_property("root_tiles_x", 1)
            overlay.set_editor_property("root_tiles_y", 1)
            overlay.set_editor_property("rectangle_west", info.bbox_west_deg)
            overlay.set_editor_property("rectangle_south", info.bbox_south_deg)
            overlay.set_editor_property("rectangle_east", info.bbox_east_deg)
            overlay.set_editor_property("rectangle_north", info.bbox_north_deg)
            overlay.set_editor_property("minimum_level", 0)
            overlay.set_editor_property("maximum_level", 0)
            overlay.set_maximum_texture_size(4096)
            # The component auto-activated at creation with default (empty) properties; Refresh() re-adds
            # it to the tileset so it picks up the URL/projection/rectangle just set. Without this the
            # terrain streams untextured -- the exact failure we hit before this fix.
            overlay.refresh()

    unreal.log("[MantlePlace] Cesium stream wired -> {}".format(info.cesium_terrain_url))
    return tileset


def stop_stream():
    """Stop the local bundle server."""
    unreal.MantlePlaceImporterLibrary.stop_bundle_stream()
