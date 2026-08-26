# Copyright Mantle Place. All Rights Reserved.
"""
Stream a Mantle Place ETL bundle into Cesium for Unreal.

Calls the C++ UMantlePlaceImporterLibrary.stream_bundle_into_cesium(zip) -- which extracts the bundle's
own Cesium-ready quantized-mesh terrain + imagery and hosts them on a local loopback server -- then
spawns an ACesium3DTileset (from the served layer.json URL) and a single-tile imagery raster overlay
under the level's CesiumGeoreference -- whose origin this module SETS from the bundle manifest, see
_ensure_georeference -- alongside Cesium World Terrain for apples-to-apples QA.

This is the "stream locally into Unreal via Cesium" capability that complements the native-asset import.
Download-to-own: nothing is streamed from the platform, only from the user's local copy of the bundle.

Usage (Python console, or an Editor Utility Widget "Stream into Cesium" button via Execute Python):
    import mantleplace_cesium_stream as mp
    mp.stream_into_cesium(r"C:/path/to/bundle.zip")
    mp.ground_truth_overlay(r"C:/path/to/bundle.zip")   # Cesium World Terrain + imagery, for QA

NOTE: the exact Cesium-for-Unreal Python property/enum names below are validated against v2.22.1 during
the editor step; risky calls are wrapped so a name mismatch degrades to a warning rather than aborting.

THE FRAME (Mantle Place world convention: North -> +X, East -> +Y, Up -> +Z)
--------------------------------------------------------------------------------
Cesium places content in an **East-South-Up** frame: +X East, +Y South, so north is -Y. That is
deliberate -- ENU is right-handed and Unreal is left-handed, so exactly one horizontal axis has to
flip or the result is a mirror. Mantle Place flips the other way round, to **North-East-Up**: +X
North, +Y East. Both are left-handed; they differ by a clean +90 deg yaw and nothing else.

Cesium offers no knob for this: ACesiumGeoreference hardcodes East/South/Up
(UCesiumEllipsoid::CreateCoordinateSystem) and derives the tileset transform from the georeference
alone, ignoring the georeference actor's own rotation. What DOES work is rotating the tileset actor:
its primitives are positioned with SetRelativeTransform, so the actor's transform composes. Hence
CESIUM_TO_WORLD_YAW below, applied to every tileset this module spawns.

Getting the sign wrong mirrors rather than rotates, which is the whole class of bug this convention exists
to prevent -- so a residual ROTATION here means this yaw is wrong, while a residual FLIP means
something upstream is.
"""
import json
import zipfile

import unreal

_TILESET_LABEL = "MP_CesiumStream_Terrain"
_QA_TERRAIN_LABEL = "MP_CesiumQA_WorldTerrain"

#: Cesium's East-South-Up -> our North-East-Up. East(+X) -> +Y and South(+Y) -> -X, which is yaw +90.
CESIUM_TO_WORLD_YAW = 90.0

#: Cesium World Terrain and Bing Maps Aerial. Both need a signed-in Cesium ion account
#: (Window -> Cesium -> "Connect to Cesium ion").
ION_ASSET_WORLD_TERRAIN = 1
ION_ASSET_BING_AERIAL = 2


def _find_georeference():
    """Return the level's CesiumGeoreference (the existing CES_Georeference), or None."""
    for actor in unreal.EditorActorSubsystem().get_all_level_actors():
        if isinstance(actor, unreal.CesiumGeoreference):
            return actor
    return None


def _ensure_georeference(zip_path, geoid_separation_m=0.0):
    """Find or spawn the level's CesiumGeoreference and set its origin from the bundle manifest.

    The +90 yaw in _orient_into_world_frame pivots on the tileset actor's origin at UE (0,0,0),
    which is wherever the georeference origin says it is. That is the right pivot only when the
    georeference origin IS the bundle's AOI origin: a hand-set or leftover origin parks the streamed
    patch away from (0,0,0), and the yaw then swings it around the globe instead of spinning it in
    place. So this module owns the origin -- applied from the manifest, never trusted from the
    level (issue #30).

    If the manifest origin cannot be read, the existing georeference is left untouched -- correct
    only if it was already set to the bundle origin by hand.
    """
    georef = _find_georeference()
    if georef is None:
        unreal.log_warning("[MantlePlace] No CesiumGeoreference in the level; spawning a default one.")
        georef = unreal.EditorActorSubsystem().spawn_actor_from_class(
            unreal.CesiumGeoreference, unreal.Vector(0, 0, 0))

    origin = _manifest_origin(zip_path) if zip_path else None
    if origin is not None:
        lon, lat, _ground_h = origin
        # Height: the ELLIPSOID (0), not the ground. The level's frame carries true orthometric Z
        # -- the imported landscape's location_z_offset_cm puts the ground at ~ground_h * 100 UE-cm
        # -- and the bundle's terrain tiles carry those same orthometric metres, so an origin at
        # height 0 lands the streamed ground on the imported ground. An origin at ground_h instead
        # sinks every Cesium overlay by the full ground elevation: invisible on a sea-level AOI,
        # kilometres on a mountain one. `geoid_separation_m` nudges the origin for content whose
        # heights are TRUE ellipsoidal (Cesium World Terrain); elevation-only either way.
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


def _orient_into_world_frame(tileset):
    """Yaw a Cesium tileset out of Cesium's East-South-Up and into our North-East-Up world.

    Pivots on the actor origin, which sits at UE (0,0,0) == the georeference origin == the AOI
    centroid, so this is a rotation about the right point and nothing translates. It lands on the
    tiles because Cesium positions its primitives with SetRelativeTransform under the tileset root,
    so the actor's own transform composes -- unlike the georeference's, which Cesium ignores.

    CAVEAT: in a `-run=pythonscript` commandlet this silently no-ops. That is not a Cesium quirk --
    set_actor_rotation returns True and leaves yaw at 0 for a plain StaticMeshActor there too,
    because components are never fully registered. Verify this yaw in a real editor session, not
    headlessly.
    """
    # unreal.Rotator's POSITIONAL argument order is (roll, pitch, yaw) -- NOT C++ FRotator's
    # (pitch, yaw, roll). A positional 90 in the middle slot is a PITCH, which tips the whole
    # globe onto its side about the actor origin (issue #30). Keywords only, here and anywhere
    # else a Rotator is built.
    tileset.set_actor_rotation(
        unreal.Rotator(roll=0.0, pitch=0.0, yaw=CESIUM_TO_WORLD_YAW), False)
    unreal.log("[MantlePlace] {} yawed {:+.0f} deg: Cesium East-South-Up -> world North-East-Up."
               .format(tileset.get_actor_label(), CESIUM_TO_WORLD_YAW))


def _manifest_origin(zip_path):
    """(lon, lat, ground_orthometric_h_m) from the bundle manifest, or None.

    MPB 1.0.0 moved everything host-specific under `hosts.<hostId>`, so the origin lives at
    `hosts.unreal.georeference.origin`. The retired top-level `unreal` block is deliberately NOT
    consulted -- same clean break as the C++ manifest reader; a fallback is how a clean break
    quietly becomes dual-parsing.
    """
    try:
        with zipfile.ZipFile(zip_path) as archive:
            manifest = json.loads(archive.read("Metadata/manifest.json"))
        origin = manifest["hosts"]["unreal"]["georeference"]["origin"]
        return origin["lon"], origin["lat"], origin.get("ground_orthometric_h_m", 0.0)
    except Exception as exc:  # noqa: BLE001 - QA helper: degrade to "leave the georeference alone"
        unreal.log_warning("[MantlePlace] could not read the manifest origin: {}".format(exc))
        return None


def stream_into_cesium(zip_path, geoid_separation_m=0.0):
    """Start the local server (C++) and wire up the Cesium tileset + imagery overlay. Returns the tileset.

    Owns the CesiumGeoreference origin: sets it to the bundle manifest's AOI origin (spawning a
    georeference if the level has none), so the streamed patch lands over UE (0,0,0) -- coincident
    with the imported copy -- and the world-frame yaw spins it in place. The bundle's terrain tiles
    carry orthometric metres like the level does, so leave `geoid_separation_m` at 0 for an exact
    overlay; it exists for symmetry with ground_truth_overlay and is elevation-only either way.
    """
    info = unreal.MantlePlaceImporterLibrary.stream_bundle_into_cesium(zip_path)
    if not info.success:
        unreal.log_error("[MantlePlace] stream failed: {}".format(info.message))
        return None
    unreal.log("[MantlePlace] {}".format(info.message))

    georef = _ensure_georeference(zip_path, geoid_separation_m)

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

    # The streamed terrain is Cesium-framed like any other tileset, so it needs the same yaw as the
    # ion assets do. Without it the bundle's own terrain sits 90 deg off its natively-imported twin --
    # the two would disagree about north while both being "correct" in their own frame.
    _orient_into_world_frame(tileset)

    unreal.log("[MantlePlace] Cesium stream wired -> {}".format(info.cesium_terrain_url))
    return tileset


def ground_truth_overlay(zip_path, geoid_separation_m=0.0):
    """Drop Cesium World Terrain + Bing aerial over the AOI, in OUR frame, for visual QA.

    This is the external check on a native import: real-world terrain and imagery, georeferenced from
    the bundle's own manifest origin and yawed into the world frame, sitting on top of the imported
    landscape. A coastline that coincides is the acceptance evidence; one that is rotated means
    CESIUM_TO_WORLD_YAW is wrong, and one that is MIRRORED means an importer bug.

    Requires a signed-in Cesium ion account: Window -> Cesium -> "Connect to Cesium ion". (The
    sign-in lives on the "Cesium" tab, which a saved layout can leave stacked behind "Cesium ion
    Assets" -- if you cannot see the button, that is why.)

    The georeference origin sits at the ellipsoid (see _ensure_georeference), so level content at
    true orthometric Z meets ion terrain at true ellipsoidal Z. `geoid_separation_m` is the local
    geoid height (signed; about -32 m around San Francisco Bay): pass it to cancel the resulting
    orthometric-vs-ellipsoidal offset, or leave it 0 and expect roughly that offset. Either way it
    is elevation-only and cannot affect the horizontal comparison this function exists for -- do
    not "fix" it by rotating or mirroring anything.
    """
    georef = _ensure_georeference(zip_path, geoid_separation_m)

    _remove_existing(_QA_TERRAIN_LABEL)
    tileset = unreal.EditorActorSubsystem().spawn_actor_from_class(
        unreal.Cesium3DTileset, unreal.Vector(0, 0, 0))
    tileset.set_actor_label(_QA_TERRAIN_LABEL)
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
    """Stop the local bundle server."""
    unreal.MantlePlaceImporterLibrary.stop_bundle_stream()
