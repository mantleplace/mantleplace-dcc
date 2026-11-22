# Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.
import glob
import json
import os
import zipfile

import unreal

_TILESET_LABEL = "MP_CesiumStream_Terrain"
_QA_TERRAIN_LABEL = "MP_CesiumQA_WorldTerrain"
_CLIP_LABEL = "MP_CesiumStream_AoiClip"

_OUTLINER_FOLDER = "MantlePlace/Stream"


def _file_in_outliner(actor):
    try:
        actor.set_folder_path(_OUTLINER_FOLDER)
    except Exception as exc:  # noqa: BLE001 - older Cesium/UE actor types may not expose it
        unreal.log_warning(
            "[MantlePlace] could not file {} in the outliner: {}".format(
                actor.get_actor_label(), exc))

CESIUM_TO_WORLD_YAW = 90.0

ION_ASSET_WORLD_TERRAIN = 1
ION_ASSET_BING_AERIAL = 2


def _find_georeference():
    for actor in unreal.EditorActorSubsystem().get_all_level_actors():
        if isinstance(actor, unreal.CesiumGeoreference):
            return actor
    return None


def _ensure_georeference(zip_path, geoid_separation_m=0.0, origin=None):
    georef = _find_georeference()
    if georef is None:
        unreal.log_warning("[MantlePlace] No CesiumGeoreference in the level; spawning a default one.")
        georef = unreal.EditorActorSubsystem().spawn_actor_from_class(
            unreal.CesiumGeoreference, unreal.Vector(0, 0, 0))

    if origin is not None:
        origin = (origin[0], origin[1], 0.0)
    else:
        origin = _manifest_origin(zip_path) if zip_path else None
    if origin is not None:
        lon, lat, _ground_h = origin
        try:
            georef.set_editor_property("origin_placement", unreal.OriginPlacement.CARTOGRAPHIC_ORIGIN)
            georef.set_editor_property("origin_longitude", lon)
            georef.set_editor_property("origin_latitude", lat)
            georef.set_editor_property("origin_height", geoid_separation_m)
            unreal.log("[MantlePlace] georeference origin -> {:.6f} lon, {:.6f} lat, {:.2f} m (ellipsoid)"
                       .format(lon, lat, geoid_separation_m))
        except Exception as exc:  # noqa: BLE001 - degrade to whatever the level already had
            unreal.log_warning("[MantlePlace] could not set the georeference origin: {}".format(exc))
    return georef


def _remove_existing(label):
    eas = unreal.EditorActorSubsystem()
    for actor in eas.get_all_level_actors():
        if actor.get_actor_label() == label:
            eas.destroy_actor(actor)


def _add_raster_overlay(actor, overlay_class, name):
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


def _orient_into_world_frame(tileset):
    tileset.set_actor_rotation(
        unreal.Rotator(roll=0.0, pitch=0.0, yaw=CESIUM_TO_WORLD_YAW), False)
    unreal.log("[MantlePlace] {} yawed {:+.0f} deg: Cesium East-South-Up -> world North-East-Up."
               .format(tileset.get_actor_label(), CESIUM_TO_WORLD_YAW))


def _clip_to_aoi(tileset, georef, info):
    if not info.has_bbox:
        unreal.log_warning(
            "[MantlePlace] the stream reports no AOI bbox; leaving the tileset unclipped "
            "(expect the flat zero-height root tiles out to the horizon).")
        return None

    _remove_existing(_CLIP_LABEL)
    polygon = unreal.EditorActorSubsystem().spawn_actor_from_class(
        unreal.CesiumCartographicPolygon, unreal.Vector(0, 0, 0),
        unreal.Rotator(roll=0.0, pitch=0.0, yaw=CESIUM_TO_WORLD_YAW))
    polygon.set_actor_label(_CLIP_LABEL)
    _file_in_outliner(polygon)
    try:
        polygon.globe_anchor.set_editor_property("georeference", georef)
    except Exception as exc:  # noqa: BLE001 - it resolves the level's only georeference anyway
        unreal.log_warning("[MantlePlace] could not pin the clip polygon's georeference: {}".format(exc))

    corners = (
        (info.bbox_west_deg, info.bbox_south_deg),
        (info.bbox_east_deg, info.bbox_south_deg),
        (info.bbox_east_deg, info.bbox_north_deg),
        (info.bbox_west_deg, info.bbox_north_deg),
    )
    spline = polygon.polygon
    spline.clear_spline_points(False)
    for lon, lat in corners:
        local = georef.transform_longitude_latitude_height_position_to_unreal(
            unreal.Vector(lon, lat, 0.0))
        spline.add_spline_point(local, unreal.SplineCoordinateSpace.LOCAL, False)
    spline.set_closed_loop(True, False)
    for index in range(spline.get_number_of_spline_points()):
        spline.set_spline_point_type(index, unreal.SplinePointType.LINEAR, False)
    spline.update_spline()

    clip = _add_raster_overlay(tileset, unreal.CesiumPolygonRasterOverlay, "MP_AoiClip")
    if clip is None:
        unreal.log_warning("[MantlePlace] could not create the AOI clip overlay; tileset left unclipped.")
        return None
    clip.set_editor_property("polygons", [polygon])
    clip.set_editor_property("invert_selection", True)
    clip.set_editor_property("exclude_selected_tiles", True)
    clip.refresh()

    try:
        polygon.set_is_temporarily_hidden_in_editor(True)
    except Exception as exc:  # noqa: BLE001 - a visible spline is untidy, not broken
        unreal.log_warning("[MantlePlace] could not hide the clip spline: {}".format(exc))

    unreal.log("[MantlePlace] clipped to the AOI bbox {:.6f},{:.6f} -> {:.6f},{:.6f} "
               "(no zero-height globe shell, no 1.9 km skirts).".format(
                   info.bbox_west_deg, info.bbox_south_deg,
                   info.bbox_east_deg, info.bbox_north_deg))
    return polygon


_MANIFEST_MEMBER = "Metadata/manifest.json"


def _find_member(archive, name):
    names = archive.namelist()
    if name in names:
        return name
    folded = name.casefold()
    for candidate in names:
        if candidate.casefold() == folded:
            return candidate
    raise KeyError(name)


def _read_manifest(zip_path):
    with zipfile.ZipFile(zip_path) as archive:
        return json.loads(archive.read(_find_member(archive, _MANIFEST_MEMBER)))


def _manifest_origin(zip_path):
    try:
        manifest = _read_manifest(zip_path)
        origin = manifest["hosts"]["unreal"]["georeference"]["origin"]
        return origin["lon"], origin["lat"], origin.get("ground_orthometric_h_m", 0.0)
    except Exception as exc:  # noqa: BLE001 - QA helper: degrade to "leave the georeference alone"
        unreal.log_warning("[MantlePlace] could not read the manifest origin: {}".format(exc))
        return None


def _newest_vault_cache_bundle():
    cache = os.path.join(unreal.Paths.project_saved_dir(), "MantlePlace", "VaultCache")
    candidates = glob.glob(os.path.join(cache, "*", "bundle.zip"))
    if not candidates:
        return None
    return max(candidates, key=os.path.getmtime)


_pending_fetch = None


def _info_origin(info):
    return (info.origin_longitude_deg, info.origin_latitude_deg) if info.has_origin else None


def _fetch_terrain_tiles_then_stream(zip_path, geoid_separation_m):
    global _pending_fetch
    fetcher = unreal.MantlePlaceTerrainTilesFetcher()

    def _fetched(success, tiles_path, message):
        global _pending_fetch
        _pending_fetch = None
        if not success:
            unreal.log_error("[MantlePlace] stream failed: {}".format(message))
            return
        unreal.log("[MantlePlace] {}".format(message))
        stream_into_cesium(zip_path, geoid_separation_m)

    fetcher.on_fetched.add_callable(_fetched)
    _pending_fetch = fetcher
    unreal.log("[MantlePlace] fetching the terrain tiles for this order...")
    if not fetcher.fetch(zip_path):
        _pending_fetch = None
        unreal.log_error("[MantlePlace] stream failed: a terrain tiles fetch is already running.")


def stream_into_cesium(zip_path=None, geoid_separation_m=0.0):
    if zip_path is None:
        zip_path = _newest_vault_cache_bundle()
        if zip_path is None:
            unreal.log_error(
                "[MantlePlace] no cached vault bundle to stream — import one first, "
                "or pass a bundle .zip path explicitly.")
            return None
        unreal.log("[MantlePlace] streaming the newest cached bundle: {}".format(zip_path))
    info = unreal.MantlePlaceImporterLibrary.stream_bundle_into_cesium(zip_path)
    if not info.success and info.terrain_tiles_fetchable:
        _fetch_terrain_tiles_then_stream(zip_path, geoid_separation_m)
        return None
    if not info.success:
        unreal.log_error("[MantlePlace] stream failed: {}".format(info.message))
        return None
    unreal.log("[MantlePlace] {}".format(info.message))

    georef = _ensure_georeference(zip_path, geoid_separation_m, _info_origin(info))

    _remove_existing(_TILESET_LABEL)
    tileset = unreal.EditorActorSubsystem().spawn_actor_from_class(
        unreal.Cesium3DTileset, unreal.Vector(0, 0, 0))
    tileset.set_actor_label(_TILESET_LABEL)
    _file_in_outliner(tileset)
    try:
        tileset.set_editor_property("tileset_source", unreal.TilesetSource.FROM_URL)
    except Exception as exc:
        unreal.log_warning("[MantlePlace] tileset_source set failed ({}); set it to 'From Url' by hand.".format(exc))
    tileset.set_editor_property("url", info.cesium_terrain_url)
    tileset.set_editor_property("georeference", georef)

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
            overlay.refresh()

    _orient_into_world_frame(tileset)

    _clip_to_aoi(tileset, georef, info)

    unreal.log("[MantlePlace] Cesium stream wired -> {}".format(info.cesium_terrain_url))
    return tileset


def ground_truth_overlay(zip_path, geoid_separation_m=0.0):
    georef = _ensure_georeference(zip_path, geoid_separation_m)

    _remove_existing(_QA_TERRAIN_LABEL)
    tileset = unreal.EditorActorSubsystem().spawn_actor_from_class(
        unreal.Cesium3DTileset, unreal.Vector(0, 0, 0))
    tileset.set_actor_label(_QA_TERRAIN_LABEL)
    _file_in_outliner(tileset)
    try:
        tileset.set_editor_property("tileset_source", unreal.TilesetSource.FROM_CESIUM_ION)
        tileset.set_editor_property("ion_asset_id", ION_ASSET_WORLD_TERRAIN)
    except Exception as exc:  # noqa: BLE001
        unreal.log_warning("[MantlePlace] could not point the QA tileset at ion asset {} ({}); "
                           "set it by hand.".format(ION_ASSET_WORLD_TERRAIN, exc))
    tileset.set_editor_property("georeference", georef)

    overlay = _add_raster_overlay(tileset, unreal.CesiumIonRasterOverlay, "MP_QA_BingAerial")
    if overlay is None:
        unreal.log_warning("[MantlePlace] QA terrain has no imagery overlay; terrain only.")
    else:
        try:
            overlay.set_editor_property("ion_asset_id", ION_ASSET_BING_AERIAL)
            overlay.refresh()
        except Exception as exc:  # noqa: BLE001
            unreal.log_warning("[MantlePlace] could not configure the QA imagery overlay: {}".format(exc))

    _orient_into_world_frame(tileset)
    unreal.log("[MantlePlace] QA overlay ready. Expect the coastline to coincide with the imported "
               "landscape in plan; a vertical offset of about the geoid separation is expected.")
    return tileset


def stop_stream():
    unreal.MantlePlaceImporterLibrary.stop_bundle_stream()
    for label in (_TILESET_LABEL, _CLIP_LABEL):
        _remove_existing(label)
