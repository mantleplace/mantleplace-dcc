---
name: import-inventory
description: What each host plugin imports from a bundle, per category — the manifest pointer it reads, what it becomes in the host, whether an import brings it in by default, what it was measured to cost and what that cost grows with, and whether a published scale plausibly fits. The home of Unreal's measured import figures; Revit's stay in code and are cited here. Read before proposing a published scale, a new default, or a cost warning for any imported layer.
---

# Import inventory

What each host plugin brings in from a bundle, one row per category: where it reads it, what it
makes, when, at what cost, and what that cost grows with. It is the inventory that
[issue 283](https://github.com/mantleplace/mantleplace-dcc/issues/283) (published scales) is decided
from, and the "Scale axis?" column is that issue's to settle: every entry in it is a hypothesis.

**What lives where.** Whether a host supports a feature at all is the
[host support matrix](host-support.md)'s, and how a feature behaves is the host's own README
([Unreal](../unreal/README.md), [Revit](../revit/README.md)). This file says which pointer, which
default, what cost and what driver, and nothing about status.

**One home per figure.**

- **Revit's figures live in code**, in `SlowStepNotice.Measured(ImportStepKind)` in
  [`SlowStepNotice.cs`](../revit/src/MantlePlace.Revit.Core/SlowStepNotice.cs), because the import
  window reads them at run time. A Revit row cites its entry by step kind and never restates a
  figure. Where a step kind has no entry, the row says "not measured". A new Revit figure goes into
  `Measured`, not here.
- **Unreal's figures have no home in code, so this file is their home.** Each one says how it was
  taken: the order, the MPB version, the Unreal version, the plugin commit, and that the editor ran
  headless with `-nullrhi`.

**Field names, not values.** Every manifest field is named as the published schema at
`https://mantle.place/.well-known/schemas/bundle-manifest/` names it. This file never restates a value
the schema owns.

## Revit

One row per row of the import window's checklist, in the order the steps run, and one for the
terrain's third tier. "When" is the checklist's starting state, `ImportLayers.OnByDefault` in
[`ImportLayers.cs`](../revit/src/MantlePlace.Revit.Core/ImportLayers.cs). An import with nobody there
to choose, the unattended path, brings in every row (`ImportLayerChoice.All`). The two subdivision
rows and the drape need the terrain row ticked.

| Category | Manifest source | Becomes | When | Measured cost | Cost driver | Scale axis? (hypothesis) | Lost at lower scales |
|---|---|---|---|---|---|---|---|
| Terrain | TIN tier: path from `layout.surface_dxf`, else `elevation.surface_dxf.path`. Points tier: `layout.points_csv`, else `elevation.points_csv.path`. For both, `hosts.revit.surface_dxf` and `hosts.revit.toposurface_points` are read first for the file's units, frame and digest, and are discarded if they name a different file | One toposolid, built on the imagery type when the drape is ticked | On by default | `Measured(ToposurfaceFromSurfaceTin)`, the same entry as `ToposurfaceFromPointsFile`: built on the imagery type, as the default import builds it | Vertex count. Published as `hosts.revit.surface_dxf.triangle_count` and `hosts.revit.toposurface_points.point_count`. **Precedent:** `hosts.revit.surface_dxf.decimated_from_triangle_count` discloses one platform decimation already | Point-density tiers | Fine relief. Every other layer stands on the ground ([ADR 0004](adr/0004-revit-terrain-identity.md)), so this is the hardest scale to change after an import |
| Terrain, third tier | The same file as the TIN tier, as a CAD link | A linked CAD instance, which the curator converts to a toposolid | Only when neither the TIN nor the points can be built | Not measured: `ToposurfaceFromSurfaceDxf` has no entry | Triangle count, as above | None: a fallback, not a choice | — |
| Published contours | `hosts.revit.contours.path` only. No fallback | One DirectShape per contour, on the Topography category's "Published Contours" subcategory ([ADR 0013](adr/0013-revit-published-contours-are-directshapes.md)) | Off by default | `Measured(PublishedContours)` | Contour count. Not published; it follows `elevation.contours.interval_m` | Interval tiers. The step takes seconds, so the case is legibility, not time | Contour density |
| Context buildings | Path from `layout.buildings_ifc`, else `buildings.ifc.path`. `hosts.revit.ifc_site` is read first for units, frame and digest | One Generic Model DirectShape per building, copied out of the converted site model ([ADR 0012](adr/0012-context-buildings-come-from-the-site-model.md)) | On by default | `Measured(ContextBuildings)`, opening the IFC included | Building count. Published as `hosts.revit.ifc_site.footprint_count` and `buildings.footprint_count`. **Precedent:** `buildings.decimated` | Count tiers (within a radius, above a height), or level-of-detail tiers | Distant and small context |
| Site model | The same file as the context buildings | An IFC link. Revit converts the IFC to a Revit file first | Off by default | `Measured(LinkSiteIfc)`: the conversion, once per order and Revit version. A later import reuses the converted file, and so does this import when the context buildings are ticked too, since their step saves the conversion it opens | IFC size: `hosts.revit.ifc_site.footprint_count` and `hosts.revit.ifc_site.terrain_triangle_count` | None: the same buildings as the row above, as a link | — |
| Road centrelines | `hosts.revit.vectors.layers[name=road_splines].path`. The shared `vector.layers[name=road_splines]` GeoJSON only on a bundle whose block has no `vectors` | One DirectShape of linework per road, on the Roads category | On by default | `Measured(RoadCentrelines)`, timed together with the shared coordinates and the site location | Feature count: `hosts.revit.vectors.layers[].feature_count` | None: seconds | — |
| Water subdivisions | `hosts.revit.vectors.layers[name=water].path`, polygons only, with the same fallback | One subdivision per water body, its islands left as holes | On by default | `Measured(Water)`. Typing at the cut: `MeasuredTypeAtCut(GroundLayer.Water)` | Water bodies cut | None: seconds | — |
| Road surface subdivisions | `hosts.revit.vectors.layers[name=road_polygons].path`, with the same fallback | One subdivision per polygon, city blocks left as holes | Off by default | `Measured(RoadPolygons)`, the longest step of any, with its `Saves`. Typing at the cut: `MeasuredTypeAtCut(GroundLayer.RoadSurface)` | Polygons cut. A published feature is a multipolygon per road class, so on the measured order `feature_count` is a small fraction of the subdivisions cut. Every later cut costs more as more exist | Count tiers derived by the platform, or another representation. A prefix of polygons ranked by area would leave holes in the network | Narrow roads, the road network's edges in the terrain |
| Planting | `layout.tree_points`, else `landcover.tree_points.path`. Host-neutral: the Revit block has no copy | One Planting family instance per tree point, of the tree or the shrub family, committed in chunks (`ImportChunking.ElementsPerTransaction`) | On by default | `Measured(Vegetation)` | Tree count: `landcover.tree_points.point_count`, an upper bound, because rows the families cannot build are left out (`PlantingFamilies.Fits`). **Precedent:** `landcover.tree_points.truncated_to_tallest`, present only when the platform capped the layer, is one published scale already. Every tree also weighs on the model after the import | A progressive rank (any prefix an even subset), or density tiers | Canopy density, and how the site reads in a render |
| Land use plan | `hosts.revit.vectors.layers[name=land_use].path`, with the same fallback | Filled regions on a land use plan of its own, one per published polygon, in one filled region type per published `subtype`, with a key ([ADR 0015](adr/0015-revit-subdivisions-are-for-built-surfaces.md)) | Off by default | `Measured(LandUse)`, on two reference bundles rather than this order, as its remarks say | Polygon count: `hosts.revit.vectors.layers[].feature_count` | None: context on a plan, not the model | — |
| Land cover plan | `hosts.revit.vectors.layers[name=land_cover].path`, with the same fallback | As land use, on a land cover plan of its own | Off by default | `Measured(LandCover)`, as the land use | As land use | None | — |
| Flood zones | `hosts.revit.vectors.layers[name=flood_zones].path` only. No fallback | Filled regions on a hazard plan ([ADR 0014](adr/0014-revit-hazards-are-drawn-on-a-hazard-plan.md)) | Off by default | `Measured(FloodZones)`, on a hand-made bundle rather than this order, as its remarks say | Zone count: `hosts.revit.vectors.layers[].feature_count`, and `flood.nfhl.feature_count` | None: context on a plan, not the model | — |
| Steep ground | `hosts.revit.vectors.layers[name=steep_slope].path` only. No fallback | Hatched filled regions on the same hazard plan | Off by default | `Measured(SteepGround)`, as the flood zones | Polygon count: `hosts.revit.vectors.layers[].feature_count`, and `elevation.steep_slope.feature_count` | None | — |
| Imagery drape | `hosts.revit.drape.path`. The shared `layout.imagery_drape` only on a bundle whose block has no drape | A material whose appearance asset holds the photograph, worn by the terrain's toposolid type and by every subdivision ([ADR 0008](adr/0008-revit-drape-is-anchored-to-the-smooth-shading-origin.md)) | On by default | `Measured(ImageryDrape)`, Revit 2025 alone. Revit 2026 and 2027: the commit after typed cuts, `MeasuredDrapeCommitSeconds` | Subdivision count, not pixels: the cost is the subdivisions taking the photograph. Pixel count is published as `hosts.revit.drape.width` and `hosts.revit.drape.height`, with `hosts.revit.drape.effective_gsd_m` | Resolution tiers by ground sample distance, **only if a measurement shows they save time**. A scale that only blurs the photograph is a regression | Sharpness up close |

**Steps that import no content.** These run on every import and have no checklist row:

| Step | Manifest source | Becomes | Measured cost |
|---|---|---|---|
| Shared coordinates | `hosts.revit.georeference.origin.projected`, else `delivery.local_origin` | The project's survey point | `Measured(SetSharedCoordinates)`, timed with the next two |
| Site location | `hosts.revit.georeference.origin.lat` and `.lon`, and `location.time_zone`. No fallback | The project's site location and time zone | `Measured(SetSiteLocation)` |
| Site context view | None | A 3D view and a view filter over everything an import stamped | `Measured(SiteContextView)`. Its unexplained time in Revit 2026 and 2027 is [issue 258](https://github.com/mantleplace/mantleplace-dcc/issues/258)'s, and is no layer's cost |
| Attribution and provenance | `attribution.sources[]`, `order_id`, `job_id`, `version` | A drafting view of credits, and the provenance record ([ADR 0011](adr/0011-revit-provenance-record-and-attribution-note-identity.md)) | `Measured(AttributionAndProvenance)` |

**Cost that no single row owns.** Smooth shading is committed across the terrain and every
subdivision on it. With the drape ticked that happens before the first cut, on a bare terrain;
otherwise it happens after the last step, where it grows with the subdivisions cut:
`MeasuredSmoothShadingWith405` and `MeasuredSmoothShadingWith61`.

**The plugin reads none of these counts for cost; it reads a level's `cost_driver`.** The Revit core parses `triangle_count`,
`terrain_triangle_count`, `footprint_count` and the tree points' `point_count`, but nothing reads
them. Every notice counts from the files at run time. The one published count it does read is a
fidelity level's `cost_driver`, in the `levels` block beside each of these pointers: the import
window multiplies it by the step's measured cost per unit, `Measured(kind).PerUnit`, which Planting
and Road surface subdivisions carry today, and shows the count alone for every other row.

### In the bundle and not imported by Revit

| Pointer | Why not |
|---|---|
| `layout.landxml`, `elevation.landxml` | The Civil 3D path. Listed as available, never imported |
| `layout.contours`, `elevation.contours` | The shared contours state no frame. Only the block's own copy is placed |
| The line features of `water` | Turning a stream centreline into an area means inventing a width, which is derivation |
| `vector.layers[name=road]`, and every other shared layer | Only the five layers above are looked up. The `road` layer's presence only explains a missing `road_polygons` |
| `flood.nfhl`, `elevation.steep_slope` | GeoPackages this host does not read. Their copies in the block are imported |
| `buildings.formats[]` (the building mesh and footprints) | The context buildings come from the site model ([ADR 0012](adr/0012-context-buildings-come-from-the-site-model.md)) |
| `elevation.dem` | Not imported. Only its `bounds_target_crs` and `crs` are read, as the last fallback for a shared drape's extent |
| `layout.cesium_terrain`, basemap and imagery tiles, coverage rasters | No Revit construct takes them ([host support matrix](host-support.md)) |
| `hosts.unreal` | A sibling host's block, never read (`HPS-36`) |

## Unreal

One row per category the importer brings in. "When" follows the import panel's mode picker, which
starts at Landscape: the landscape and its paint layers come with Landscape or Both, the terrain mesh
with Mesh or Both, and every other row comes in whenever the bundle carries it, in every mode.
Cesium streaming is a separate request.

**What was measured, and why most rows were not.** Order `4276ef78`'s cached bundle (MPB 1.6.0) has
`packaging.delivery_model` `base_on_demand`, and its `hosts.unreal` block carries `readiness` and
nothing else. The import
refuses it before anything is extracted: the bundle has not generated its Unreal formats. That was
confirmed headless in two runs, in each of the three import modes, in UE 5.8.2 with the plugin at
`b0251a1`. Bringing those formats in is a Prepare for Unreal, which needs a sign-in, so every
native-import row below is "not measured" for that reason. The one category the plugin reads from this
zip as it stands, the Cesium stream, was measured.

| Category | Manifest source | Becomes | When | Measured cost | Cost driver | Scale axis? (hypothesis) | Lost at lower scales |
|---|---|---|---|---|---|---|---|
| Landscape | `hosts.unreal.heightmap.path`, with its `resolution` and `landscape_transform`. No fallback | One Landscape actor, built with the drape material assigned | On by default | Not measured: the cached bundle carries no Unreal formats (above) | Vertex count: `hosts.unreal.heightmap.resolution`, with `landscape_transform.component_count_x` and `component_count_y` | Heightmap resolution tiers | Fine relief |
| Paint layers | `hosts.unreal.landscape_layers.material_weights.ue_ready[].path`, with its band legend | One landscape layer info per paint layer, its weights painted on the landscape | With the landscape: on by default | Not measured, as above | Pixels resampled: `ue_ready[].width` and `height`, times the paint layers, onto the landscape grid | None of its own: it follows the landscape grid | — |
| Imagery drape | `hosts.unreal.imagery_drape.source`, with its `extent`. No fallback | A texture and a material instance, on the landscape and the terrain mesh | Always | Not measured, as above | Pixel count. Not published for this block, which carries `effective_gsd_m` only | Resolution tiers by ground sample distance, for memory and file size if not import time | Sharpness up close |
| Coverage rasters | `hosts.unreal.landscape_layers.<name>.ue_ready[].path`, for every layer but `material_weights` | One data texture per raster, carrying its value mapping | Always | Not measured, as above | `ue_ready[].width`, `height` and `size_bytes` | Resolution tiers, if a material or graph needs less than the landscape grid | Detail in data a material samples |
| Terrain mesh | `hosts.unreal.mesh_alternative.path` | A static mesh with Nanite on, and one actor | On request (Mesh or Both) | Not measured, as above | Triangle count. Not published for this pointer | Mesh level-of-detail tiers | Detail up close |
| Buildings | `hosts.unreal.buildings_mesh.path`, its digest from `buildings.formats[]` | One static mesh of every building, and one actor | Always | Not measured, as above | `buildings.triangle_count` and `buildings.footprint_count`. **Precedent:** `buildings.decimated` | Level-of-detail or count tiers | Distant and small context |
| Road splines | `vector.layers[name=road_splines].formats[format=geojson].path`. Host-neutral: the Unreal block carries no copy | One spline actor per road, its width, class and name as tags | Always | Not measured: the import refuses the bundle before this layer, although the layer is in it | Feature count: `vector.layers[].feature_count` | None: one actor per road | — |
| Tree points | `hosts.unreal.foliage_points.path`, its `point_count` checked against the rows | One data table of rows. No actors: scattering them is [issue 76](https://github.com/mantleplace/mantleplace-dcc/issues/76) | Always | Not measured, as above | `hosts.unreal.foliage_points.point_count`. **Precedent:** `landcover.tree_points.truncated_to_tallest`, on the same trees' shared pointer, which this host does not read | A progressive rank, or density tiers. The import makes a table, so the cost to scale is likely the scatter's instances, not the import | Canopy density |
| Cesium stream | `layout.cesium_terrain` and `cesium_terrain.tile_count`. The drape's URL from `hosts.unreal.imagery_drape.source`, absent on this bundle | The terrain subtree under `layout.cesium_terrain`'s directory, plus the drape file when the stream serves one, extracted into the project's `Saved` folder and served on loopback. Nothing is extracted by folder name. The editor's Python helper then spawns a Cesium tileset | On request | **Staging: 0.081 s** cold, 48 entries, 73,851 bytes (47 terrain tiles and `layer.json`; no drape, so no imagery). **Repeat stream of the same bundle: 0.004 s**, reusing the staged files. One run, headless `-nullrhi`, order `4276ef78`, MPB 1.6.0, UE 5.8.2, after [issue 288](https://github.com/mantleplace/mantleplace-dcc/issues/288). Before it, staging took 0.196 to 0.205 s for 52 entries, four of them imagery the stream never served. Tile streaming at run time not measured | Tile count, and the drape's size when one is served | None at import: the tiles are a level-of-detail pyramid already, and Cesium picks the level at run time | — |

**How the figures were taken.** The editor ran headless (`UnrealEditor-Cmd` with `-unattended
-nullrhi`) and a Python script called `UMantlePlaceImporterLibrary::ImportVaultPackage` and
`StreamBundleIntoCesium` directly, timing each call. The staged folder was removed between runs, so
each run's first stream was cold. With `-nullrhi` no shader compiles and nothing renders, so a figure
here is a lower bound on what a curator's editor takes. The importer's own ledger
(`MantlePlaceImportTiming`, written to `MANTLEPLACE_IMPORT_TIMELINE_DIR` when it is set) has a row for
the terrain mesh, the buildings mesh, the drape, the weight resample and the landscape with its own
steps. The coverage rasters, the road splines and the tree points have no row of their own: their time
is inside the `inside transaction` row, so measuring them apart needs the editor log's timestamps.

### In the bundle and not imported by Unreal

| Pointer | Why not |
|---|---|
| `hosts.revit` | A sibling host's block, never read (`HPS-36`) |
| `vector.layers` other than `road_splines` | Land use, water bodies and road surfaces: [issue 260](https://github.com/mantleplace/mantleplace-dcc/issues/260). Land cover: [issue 43](https://github.com/mantleplace/mantleplace-dcc/issues/43) |
| `elevation.contours` | [Issue 261](https://github.com/mantleplace/mantleplace-dcc/issues/261) |
| `flood.nfhl`, `elevation.steep_slope` | [Issue 270](https://github.com/mantleplace/mantleplace-dcc/issues/270) |
| `layout.tree_points`, `landcover.tree_points` | The same trees as `hosts.unreal.foliage_points`, in the delivery frame rather than this host's |
| `layout.buildings_ifc`, `buildings.ifc` | The buildings come from the block's mesh |
| `layout.points_csv`, `layout.surface_dxf`, `layout.landxml` | Formats for other tools. The landscape comes from the block's heightmap |
| `elevation.dem.bounds_target_crs` | Declined ([ADR 0006](adr/0006-unreal-declines-elevation-bounds-target-crs.md)) |
| `layout.imagery_drape` | The shared drape. This host reads its block's own |
