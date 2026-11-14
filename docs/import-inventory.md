---
name: import-inventory
description: What each host plugin imports from a bundle, per category — the manifest pointer it reads, what it becomes in the host, whether an import brings it in by default, what it was measured to cost and what that cost grows with, and whether a published scale plausibly fits. The home of Unreal's measured import figures; Revit's stay in code and are cited here. Read before proposing a published scale, a new default, or a cost warning for any imported layer. Also the checklist of every value the Unreal, Revit and AutoCAD plugins read from a per-host manifest block, with the neutral 2.x member that already carries the same fact, for writing the 3.0 format text.
---

# Import inventory

What each host plugin brings in from a bundle, one row per category: where it reads it, what it
makes, when, at what cost, and what that cost grows with. Revit's fidelity levels are settled;
the "Scale axis?" column of the Unreal rows is
[issue 316](https://github.com/mantleplace/mantleplace-dcc/issues/316)'s to settle, and every entry
in it is a hypothesis.

The last section lists every value read from a host block instead of every category, and is the
checklist for the MPB 3.0 format text.

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
  taken: the fixture, the MPB version, the Unreal version, the plugin commit, and that the editor ran
  headless with `-nullrhi`.

**Field names, not values.** Every manifest field is named as the published schema at
`https://mantle.place/.well-known/schemas/bundle-manifest/` names it. This file never restates a value
the schema owns.

## Revit

One row per row of the import window's checklist, in the order the steps run, and one for the
terrain's third tier. "When" is the checklist's starting state, `ImportLayers.OnByDefault` in
[`ImportLayers.cs`](../revit/src/MantlePlace.Revit.Core/ImportLayers.cs), and the level a row opens on
where the bundle publishes levels, `ImportLayers.DefaultLevel`. Both are set by measurement: with
every box ticked at its default level, an import through the window stayed under five minutes in
Revit 2025, 2026 and 2027 on both reference orders, so every row whose default fits starts ticked
(the gate table is on [issue 300](https://github.com/mantleplace/mantleplace-dcc/issues/300)). The
margin is thin: the slowest run, the dense order in Revit 2026, took 297 s, and one level up on the
trees or the road surfaces took that order past 300 s. An
import with nobody there to choose, the unattended path, brings in every row at `MAX`
(`ImportLayerChoice.All`). The two subdivision rows and the drape need the terrain row ticked.

| Category | Manifest source | Becomes | When | Measured cost | Cost driver | Scale axis? (hypothesis) | Lost at lower scales |
|---|---|---|---|---|---|---|---|
| Terrain | TIN tier: path from `layout.surface_dxf`, else `elevation.surface_dxf.path`. Points tier: `layout.points_csv`, else `elevation.points_csv.path`. For both, `hosts.revit.surface_dxf` and `hosts.revit.toposurface_points` are read first for the file's units, frame and digest, and are discarded if they name a different file | One toposolid, built on the imagery type when the drape is ticked | On by default | `Measured(ToposurfaceFromSurfaceTin)`, the same entry as `ToposurfaceFromPointsFile`: built on the imagery type, as the default import builds it | Vertex count. Published as `hosts.revit.surface_dxf.triangle_count` and `hosts.revit.toposurface_points.point_count`. **Precedent:** `hosts.revit.surface_dxf.decimated_from_triangle_count` discloses one platform decimation already | Point-density tiers | Fine relief. Every other layer stands on the ground ([ADR 0004](adr/0004-revit-terrain-identity.md)), so this is the hardest scale to change after an import |
| Terrain, third tier | The same file as the TIN tier, as a CAD link | A linked CAD instance, which the curator converts to a toposolid | Only when neither the TIN nor the points can be built | Not measured: `ToposurfaceFromSurfaceDxf` has no entry | Triangle count, as above | None: a fallback, not a choice | — |
| Published contours | `hosts.revit.contours.path` only. No fallback | One DirectShape per contour, on the Topography category's "Published Contours" subcategory ([ADR 0013](adr/0013-revit-published-contours-are-directshapes.md)) | On by default | `Measured(PublishedContours)` | Contour count. Not published; it follows `elevation.contours.interval_m` | Interval tiers. The step takes seconds, so the case is legibility, not time | Contour density |
| Context buildings | Path from `layout.buildings_ifc`, else `buildings.ifc.path`. `hosts.revit.ifc_site` is read first for units, frame and digest | One Generic Model DirectShape per building, copied out of the converted site model ([ADR 0012](adr/0012-context-buildings-come-from-the-site-model.md)) | On by default | `Measured(ContextBuildings)`, opening the IFC included | Building count. Published as `hosts.revit.ifc_site.footprint_count` and `buildings.footprint_count`. **Precedent:** `buildings.decimated` | Count tiers (within a radius, above a height), or level-of-detail tiers | Distant and small context |
| Site model | The same file as the context buildings | An IFC link. Revit converts the IFC to a Revit file first | Off by default | `Measured(LinkSiteIfc)`: the conversion, once per order and Revit version. A later import reuses the converted file; with the context buildings ticked, so does this one | IFC size: `hosts.revit.ifc_site.footprint_count` and `hosts.revit.ifc_site.terrain_triangle_count` | None: the same buildings as the row above, as a link | — |
| Road centrelines | `hosts.revit.vectors.layers[name=road_splines].path`. The shared `vector.layers[name=road_splines]` GeoJSON only on a bundle whose block has no `vectors` | One DirectShape of linework per road, on the Roads category | On by default | `Measured(RoadCentrelines)`, timed together with the shared coordinates and the site location | Feature count: `hosts.revit.vectors.layers[].feature_count` | None: seconds | — |
| Water subdivisions | `hosts.revit.vectors.layers[name=water].path`, polygons only, with the same fallback | One subdivision per water body, its islands left as holes | On by default | `Measured(Water)`. Typing at the cut: `MeasuredTypeAtCut(GroundLayer.Water)` | Water bodies cut | None: seconds | — |
| Road surface subdivisions | `hosts.revit.vectors.layers[name=road_polygons].path`, with the same fallback | One subdivision per polygon, city blocks left as holes | On by default, at `MIN` | `Measured(RoadPolygons)`, and its `PerUnit` per cut, from which the window estimates each level. At `MAX` on a dense order it is the longest step of any, which is why it opens on `MIN`. Typing at the cut: `MeasuredTypeAtCut(GroundLayer.RoadSurface)` | Polygons cut. A published feature is a multipolygon per road class, so on the measured order `feature_count` is a small fraction of the subdivisions cut. Every later cut costs more as more exist | Count tiers derived by the platform, or another representation. A prefix of polygons ranked by area would leave holes in the network | Narrow roads, the road network's edges in the terrain |
| Planting | `layout.tree_points`, else `landcover.tree_points.path`. Host-neutral: the Revit block has no copy | One Planting family instance per tree point, of the tree or the shrub family, committed in chunks (`ImportChunking.ElementsPerTransaction`) | On by default, at `MIN` | `Measured(Vegetation)`, and its `PerUnit` per tree | Tree count: `landcover.tree_points.point_count`, an upper bound, because rows the families cannot build are left out (`PlantingFamilies.Fits`). **Precedent:** `landcover.tree_points.truncated_to_tallest`, present only when the platform capped the layer, is one published scale already. Every tree also weighs on the model after the import | A progressive rank (any prefix an even subset), or density tiers | Canopy density, and how the site reads in a render |
| Land use plan | `hosts.revit.vectors.layers[name=land_use].path`, with the same fallback | Filled regions on a land use plan of its own, one per published polygon, in one filled region type per published `subtype`, with a key ([ADR 0015](adr/0015-revit-subdivisions-are-for-built-surfaces.md)) | On by default | `Measured(LandUse)`, on two reference bundles rather than this order, as its remarks say | Polygon count: `hosts.revit.vectors.layers[].feature_count` | None: context on a plan, not the model | — |
| Land cover plan | `hosts.revit.vectors.layers[name=land_cover].path`, with the same fallback | As land use, on a land cover plan of its own | On by default | `Measured(LandCover)`, as the land use | As land use | None | — |
| Flood zones | `hosts.revit.vectors.layers[name=flood_zones].path` only. No fallback | Filled regions on a hazard plan ([ADR 0014](adr/0014-revit-hazards-are-drawn-on-a-hazard-plan.md)) | On by default | `Measured(FloodZones)`, on a hand-made bundle rather than this order, as its remarks say | Zone count: `hosts.revit.vectors.layers[].feature_count`, and `flood.nfhl.feature_count` | None: context on a plan, not the model | — |
| Steep ground | `hosts.revit.vectors.layers[name=steep_slope].path` only. No fallback | Hatched filled regions on the same hazard plan | On by default | `Measured(SteepGround)`, as the flood zones | Polygon count: `hosts.revit.vectors.layers[].feature_count`, and `elevation.steep_slope.feature_count` | None | — |
| Imagery drape | `hosts.revit.drape.path`. The shared `layout.imagery_drape` only on a bundle whose block has no drape | A material whose appearance asset holds the photograph, worn by the terrain's toposolid type and by every subdivision ([ADR 0008](adr/0008-revit-drape-is-anchored-to-the-smooth-shading-origin.md)) | On by default | `Measured(ImageryDrape)`, Revit 2025 alone. Revit 2026 and 2027: the commit after typed cuts, `MeasuredDrapeCommitSeconds` | Subdivision count, not pixels: the cost is the subdivisions taking the photograph. Pixel count is published as `hosts.revit.drape.width` and `hosts.revit.drape.height`, with `hosts.revit.drape.effective_gsd_m` | Resolution tiers by ground sample distance, **only if a measurement shows they save time**. A scale that only blurs the photograph is a regression | Sharpness up close |

**The vector rows read the shared carrier first.** Where `hosts.revit.vectors.gpkg` is published, each vector row above (roads, water, road surface, land use, land cover, flood zones, steep ground) reads its layer as the table of that name in the carrier (`path`, `tables`, `sha256`, `size_bytes`), in the origin's projected frame and unit, and falls back to the layer's GeoJSON `path` when the carrier has no table for the layer, or the carrier cannot be read (the reason is said in the import's log). A `gpkg` pointer without a `sha256` refuses the bundle. The building footprints table is carried but not drawn: the context buildings come from the site model ([ADR 0012](adr/0012-context-buildings-come-from-the-site-model.md)).

**A 2.0 bundle reads the projected twins.** MPB 2.0 removes `hosts.revit.vectors`. On a 2.x manifest each of roads, water, road surface, land use and land cover reads the `-Projected` twin that `layout.projected_variants` names for the layer's `gpkg` row in `vector.layers`, as the table of the layer's name (or, where the row lists `tables`, the one of the geometry the row is drawn from, `Water_polygon` for water). A row that lists no `tables` holds the layer alone, and the file's one table is read whatever the producer names it (`RoadSplines`, in CamelCase, for `road_splines`); a file of several tables is never read as its only one, in the origin's projected frame and unit, and falls back to the layer's shared GeoJSON where the twin cannot be read. A layer with no twin reads its GeoJSON. Flood zones and steep ground read `layout.flood_zones` and `layout.steep_slope`, or the twin of either where one is named. Every path is matched to the archive without regard to case.

**Steps that import no content.** These run on every import and have no checklist row:

| Step | Manifest source | Becomes | Measured cost |
|---|---|---|---|
| Shared coordinates | `hosts.revit.georeference.origin.projected`, else `delivery.local_origin` | The project's survey point, set by the first bundle only. A later bundle in the same project is built at its offset from that point, or refused ([Revit README](../revit/README.md)) | `Measured(SetSharedCoordinates)`, timed with the next two |
| Site location | `hosts.revit.georeference.origin.lat` and `.lon`, and `location.time_zone`. No fallback | The project's site location and time zone | `Measured(SetSiteLocation)` |
| Site context view | None | A 3D view and a view filter over everything an import stamped | `Measured(SiteContextView)`, made before the subdivisions. Made after them, as it was on the measured order, it cost tens of seconds in Revit 2026 and 2027 |
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

One row per category the plugin brings in. "When" follows the import panel's mode picker, which
starts at Landscape: the landscape and its paint layers come with Landscape or Both, the terrain mesh
with Mesh or Both, and every other row comes in whenever the bundle carries it, in every mode.
Cesium streaming is a separate request.

**What was measured, and why most rows were not.** Every Unreal figure below is one run, at one old plugin
commit (`b0251a1`), on one MPB 1.6.0 fixture in UE 5.8.2; none has been taken since. **Re-measure them**
when the Unreal plugin releases a track that changes an import step, when the plugin reads a new
bundle format, or when a fixture that carries Unreal's formats is available. A figure older than the
latest Unreal release is a dated record, not a current cost.

The reference fixture, a cached MPB 1.6.0 bundle, has
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
| Road splines | `hosts.unreal.road_splines.path` first, the engine-datum copy (a 2.0 bundle's `-Engine` twin), published on a US order; then `vector.layers[name=road_splines].formats[format=gpkg].path`; each read through the engine's SQLiteCore, and the `format=geojson` row when the layer has no `gpkg` row or its GeoPackage cannot be read. A source is placed only where it states `EGM2008-orthometric`: a US order's shared layer is NAVD88 and is skipped by name, never shifted, so a US bundle that publishes no copy places no roads | One `AMantlePlaceRoadSpline` per road, its width, class and name as properties ([contract](../unreal/docs/road-spline-actor-contract.md)) | Always | Not measured: the import refuses the bundle before this layer, although the layer is in it | Feature count: `vector.layers[].feature_count` | None: one actor per road | — |
| Tree points | `hosts.unreal.foliage_points.path`, its `point_count` checked against the rows | One data table of typed rows, its columns a [contract](../unreal/docs/foliage-table-contract.md) a PCG graph binds by name. On request, a PCG volume and graph scattering them as mesh instances, partitioned per cell in a World Partition level | The table always; the scatter on request (a project setting) | Not measured, as above | `hosts.unreal.foliage_points.point_count`. **Precedent:** `landcover.tree_points.truncated_to_tallest`, on the same trees' shared pointer, which this host does not read | A progressive rank, or density tiers. The import makes a table, so the cost to scale is likely the scatter's instances, not the import | Canopy density |
| Public records | Every top-level family block (`parcels`, `zoning`, `flood`, `wetlands`, `soils`, `protected`, `hydrography`, `habitat`, `contamination`, `historic`, and any family not yet named), each file by the `path` its entry states or its `layout` key, never by folder: `Records/<Family>/` and the legacy `Parcels/`, `Zoning/` and `Flood/` alike. Host-neutral: the Unreal block carries no copy | One actor per family, a spline for each DXF line, laid on the Landscape. A line is drawn only where its file states its frame and the frame is this host's. A GeoPackage is verified and credited, not drawn. The credit, disclaimer, notice and each record the bundle does not deliver, with where its holder publishes it, are written to the import log | Always | Not measured, as above | DXF line count. Not published for these files | None known | — |
| Cesium stream | `layout.cesium_terrain` and `cesium_terrain.tile_count`. The drape's URL from `hosts.unreal.imagery_drape.source`, absent on this bundle | The terrain subtree under `layout.cesium_terrain`'s directory, plus the drape file when the stream serves one, extracted into the project's `Saved` folder and served on loopback. Nothing is extracted by folder name. The editor's Python helper then spawns a Cesium tileset | On request | **Staging: 0.081 s** cold, 48 entries, 73,851 bytes (47 terrain tiles and `layer.json`; no drape, so no imagery). **Repeat stream of the same bundle: 0.004 s**, reusing the staged files. One run, headless `-nullrhi`, the reference fixture, MPB 1.6.0, UE 5.8.2, after [issue 288](https://github.com/mantleplace/mantleplace-dcc/issues/288). Before it, staging took 0.196 to 0.205 s for 52 entries, four of them imagery the stream never served. Tile streaming at run time not measured | Tile count, and the drape's size when one is served | None at import: the tiles are a level-of-detail pyramid already, and Cesium picks the level at run time | — |

**How the figures were taken.** The editor ran headless (`UnrealEditor-Cmd` with `-unattended
-nullrhi`) and a Python script called `UMantlePlaceImporterLibrary::ImportVaultPackage` and
`StreamBundleIntoCesium` directly, timing each call. The staged folder was removed between runs, so
each run's first stream was cold. With `-nullrhi` no shader compiles and nothing renders, so a figure
here is a lower bound on what a curator's editor takes. The plugin's own ledger
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
| `flood.nfhl`, `elevation.steep_slope` | The flood zones are a GeoPackage, which the plugin verifies and credits with the public records but does not draw; steep ground is not read. Both: [Issue 270](https://github.com/mantleplace/mantleplace-dcc/issues/270) |
| `layout.tree_points`, `landcover.tree_points` | The same trees as `hosts.unreal.foliage_points`, in the delivery frame rather than this host's |
| `layout.buildings_ifc`, `buildings.ifc` | The buildings come from the block's mesh |
| `layout.points_csv`, `layout.surface_dxf`, `layout.landxml` | Formats for other tools. The landscape comes from the block's heightmap |
| `elevation.dem.bounds_target_crs` | Declined ([ADR 0006](adr/0006-unreal-declines-elevation-bounds-target-crs.md)) |
| `layout.imagery_drape` | The shared drape. This host reads its block's own |

## Every value read from a host block

The checklist the MPB 3.0 format text is written against
([issue 586](https://github.com/mantleplace/mantleplace-dcc/issues/586)): 3.0 has no `hosts.<host>`
block, so each row below gets a neutral home there or a line saying it is dropped. One row per value
a plugin reads out of its own block, for Revit, AutoCAD and Unreal, and the Unreal Cesium stream's
reads in a section of its own, then the values read from outside a block that a host's download view
depends on.

**Columns.**

- **Row.** `R` Revit, `A` AutoCAD, `U` Unreal's native import, `T` Unreal's Cesium stream (tiles
  mode), `O` read outside the block. Cite a row by this id. A member in a row's name is relative to
  `hosts.<host>` unless it is written with another root.
- **Read when.** `all` when every manifest the plugin accepts is read this way; `1.x` for a member the
  2.0 manifest no longer carries, which the plugin still reads for the older bundles it accepts.
- **Neutral 2.x.** Whether the neutral part of the 2.2 manifest, the members outside `hosts`, already
  states the same fact. `yes`, then the member; `partly`, then the member and what it lacks; `none`.
  A bare name such as `frame` is `file_entries[<path>].frame`, the file's own frame statement.

**How the table was produced, and how to check it again.** It was read from the source at
`ef05172`, not from a bundle, and compared with the published schemas
(`https://mantle.place/.well-known/schemas/bundle-manifest/`, versions 1.19.0, 2.0.0, 2.1.0 and 2.2.0).

1. *Host-block reads.* Every member a reader takes off the host's block object is a row.
   - Revit: `BundleManifestReader.cs` (`RevitHostBlock(root)`, then `ReadReadiness`, `ReadFileFrame`,
     `ReadGeoreference`, `ReadOwnVectors`, `ReadHazards`, `ReadImagery`, `ReadArtifacts` through
     `BuildArtifact`, `CheckRevitHashes`) and `FidelityLevelsReader.cs`. Re-check with
     `grep -n "RevitHostBlock\|hostDetail\|revit?\.\|revit\.Object" revit/src/MantlePlace.Revit.Core/BundleManifestReader.cs`
     and read each function it lands in.
   - AutoCAD: `AutoCadBlockReader.Read` and its helpers in `AutoCadBlock.cs`; what the plan then does
     with each value is `AutoCadImportPlan.cs`. The Revit `Core` sources compiled into the AutoCAD
     build are Revit's reads, not a second set.
   - Unreal: `ParseFields` of every `MantlePlaceArtifact*.cpp` handler (its block is `In.Unreal`),
     `ParseGeoreference` and `ParseCesium` in `MantlePlaceImportManifest.cpp`, and `_manifest_origin`
     in `mantleplace_cesium_stream.py`. Re-check with
     `grep -rn "In.Unreal" unreal/MantlePlace/Source/MantlePlaceEditor/Private --include=*.cpp`.
2. *What each plugin does with it* is the reader's own comment and the code that takes the parsed
   value, named in the Use column in a few words; the host's `CLAUDE.md` and README hold the rest.
3. *Neutral 2.x.* The members of `hosts.unreal`, `hosts.revit` (and `hosts.autocad`, which only 1.19.0
   declares) were listed from the published schema and set beside the neutral members that could
   hold the same fact: the neutral entries, `location.site_origin`, `delivery`, `file_entries`,
   `layout`, `layout.projected_variants` and the neutral `levels` members. A `yes` means the member
   states the same fact by its name and description in the schema; it does **not** mean a bundle's
   neutral file is the file the host block points at, because a host block can name a file in the
   host's own frame where the neutral pointer names another. A row where that can differ says so.
4. *Not rows.* Values the plugins read from outside the host blocks (the order identity, `delivery`,
   `location.time_zone`, `attribution`, `file_entries` licensing, `view`, `files`, the public
   records' `parcels` and `zoning`) are neutral already. The ones that decide whether a host download
   view is enough are the `O` rows. A member a schema declares in a block and no reader takes is
   listed after the tables.

The 2.x schemas declare `hosts.unreal` and `hosts.revit` and let other keys through; only 1.19.0
declares `hosts.autocad`. The AutoCAD rows are therefore the 1.19.0 members.

### Revit

| Row | Host | Member read | Read when | Use | Neutral 2.x |
|---|---|---|---|---|---|
| R01 | Revit | `georeference.origin.lon`, `.lat` | all | The project's site location, applied as stated | yes: `location.site_origin.longitude`, `.latitude` |
| R02 | Revit | `georeference.origin.projected.epsg`, `.easting`, `.northing`, `.linear_unit` | all | The survey point in the delivery frame; the anchor for shared coordinates and the position every layer is built against; compared with a later bundle's | yes: `location.site_origin.projected.crs`, `.x`, `.y`, `.unit` (the CRS is a reference object there, not an EPSG integer) |
| R03 | Revit | `georeference.crs_projected` | all | The CRS the survey point is in | yes: `location.site_origin.projected.crs`, and `delivery.horizontal_epsg` |
| R04 | Revit | `georeference.vertical_datum` | all | The height reference of the survey point's elevation | yes: `location.site_origin.vertical_datum` |
| R05 | Revit | `georeference.grid_rotation_deg` | all | The grid rotation, applied with the survey point; a later bundle at a different angle is refused whole | none |
| R06 | Revit | `file_frame.type` | all | Whether the block's files are absolute in a projected CRS or local to an origin; decides if a file can be placed at all | partly: `frame.coordinate_mode` (absolute or offset) in the file's own entry; per file, not per block |
| R07 | Revit | `file_frame.crs`, `.base_crs` | all | The CRS an absolute frame is in, or the CRS a local frame's origin is stated on | partly: `frame.horizontal_crs`; no member names the CRS a local origin is stated on, other than `location.site_origin.projected.crs` |
| R08 | Revit | `file_frame.origin.easting`, `.northing`, `.linear_unit` | all | The local frame's origin, in its own unit; an unreadable unit leaves the origin unplaced and the file is refused | partly: `location.site_origin.projected.x`, `.y`, `.unit`, which an offset file is measured from; stated once for the order, not per frame |
| R09 | Revit | `file_frame.horizontal_unit`, `.vertical_unit` | all | The frame's linear units, compared with every pointer's units; a disagreement is refused, never converted | partly: `frame.unit_to_metres`, `.vertical_unit_to_metres` state a factor where the block states a unit name |
| R10 | Revit | `readiness.toposurface_points`, `.ifc_site`, `.surface_dxf`, `.vectors`, `.contours`, `.flood_zones`, `.steep_slope`, each `.present` and `.reason` | all | Whether the host's copy of each part was produced, and the stated reason it was not, shown to the user | none: the neutral readiness is the order-level `completeness`, not a per-part verdict |
| R11 | Revit | `toposurface_points.path`, `surface_dxf.path`, `ifc_site.path` | all | The topography, surface and site-model files, found by pointer, first in the block and then by `layout` | partly: `layout.points_csv`, `layout.surface_dxf`, `layout.buildings_ifc` name a file each, and `frame` states its frame; no neutral member says which of two files is the one in the host's frame, except `layout.projected_variants` for vector layers |
| R12 | Revit | `toposurface_points.sha256`, `surface_dxf.sha256`, `ifc_site.sha256`, `drape.sha256`, `contours.sha256`, and the same on each vector file | all | Verifies the bytes; a block that names a deliverable without one is refused whole | partly: `files[<path>].sha256` on a bundle stored as parts; `imagery.drape.sha256` for the drape; none on a bundle stored as one archive, which is verified whole |
| R13 | Revit | `toposurface_points.units`, `surface_dxf.units`, `ifc_site.units`, `drape.units`, `contours.units` | all | The file's linear unit, which must agree with the frame | partly: `frame.unit_to_metres` (a factor, not the name) |
| R14 | Revit | `toposurface_points.horizontal_frame`, `surface_dxf.horizontal_frame`, `ifc_site.horizontal_frame`, `contours.horizontal_frame` | all | Whether this file is absolute or local, so it is placed against the survey point or the origin | partly: `frame.coordinate_mode` |
| R15 | Revit | `toposurface_points.vertical_reference`, `contours.vertical_reference` | all | Whether heights are absolute, so they are applied without an offset | partly: `frame.coordinate_mode` is one word for the whole file, so a file with an offset in X and Y and absolute heights has no neutral statement |
| R16 | Revit | `contours.horizontal_units`, `.vertical_units`, `.vertical_datum` | all | The published contours' two units and datum, each checked against the frame | partly: `elevation.contours.units`, `.vertical_datum`; `frame.unit_to_metres`, `.vertical_unit_to_metres` |
| R17 | Revit | `drape.path`, `.format`, `.extent`, `.extent_crs` | all | The drape image in the host's frame, and the ground rectangle it covers | partly: `imagery.drape.source`, `.format`, `.extent`, `.extent_crs`, `layout.imagery_drape`; the neutral drape can be the fixed-frame one, which Revit skips by name on an order in another frame |
| R18 | Revit | `toposurface_points.levels`, `surface_dxf.levels`, `ifc_site.levels`, `drape.levels`, `contours.levels`: for each of RAW, MAX, MED and MIN, `same_as` and `reason`; `cut.rows`; `cut.lowest_level_field`; a pointer level's `path`, `sha256`, `units`, `horizontal_frame`, `extent`, `extent_crs`; `cost_driver.count`, `.unit` | all | The fidelity choices the import window offers and what each costs; a level it cannot read is shown unavailable, never as the whole file | yes: a neutral `levels` member on `elevation.points_csv`, `elevation.surface_dxf`, `buildings.ifc`, `imagery.drape`, `elevation.contours`; the cut and cost shapes are shared definitions |
| R19 | Revit | `vectors.layers[].name`, `.path`, `.sha256`, `.horizontal_frame`, `.units`, `.vertical_reference`, `.levels` | 1.x | Each vector layer's own file (roads, land use, land cover, water, road surfaces, flood zones, steep ground) in the host's frame | partly: `vector.layers[].name`, `.formats[]`, `.levels` and `layout.projected_variants` (the order-CRS twin of each layer); 2.0 removed this member |
| R20 | Revit | `vectors.gpkg.path`, `.sha256`, `.tables`, `.levels` | 1.x | The shared GeoPackage carrier of the same layers, one table per layer; its frame is the order's, absolute | partly: `vector.layers[].formats[].path`, `.sha256`, `.tables`, with `layout.projected_variants` for the projected twin |

### AutoCAD

The 2.x schemas do not declare `hosts.autocad`; the plugin reads the block 1.19.0 declares. A bundle
without the block is refused with a message to prepare the order again.

| Row | Host | Member read | Read when | Use | Neutral 2.x |
|---|---|---|---|---|---|
| A01 | AutoCAD | `file_frame.type` | all | Absolute or local frame; decides the drawing-location check and whether a file is placed | partly: `frame.coordinate_mode` per file |
| A02 | AutoCAD | `file_frame.crs`, `.base_crs` | all | The CRS to compare with the drawing's geographic location and, in Civil 3D, its zone; a value that is not an EPSG code refuses the import | partly: `frame.horizontal_crs`; no member names a local frame's base CRS |
| A03 | AutoCAD | `file_frame.origin.easting`, `.northing`, `.linear_unit` | all | The local frame's origin, stated back to the user when the drawing has no location | partly: `location.site_origin.projected.x`, `.y`, `.unit` |
| A04 | AutoCAD | `file_frame.horizontal_unit`, `.vertical_unit` | all | The unit the drawing's contours and surface are stated in, named to the user | partly: `frame.unit_to_metres`, `.vertical_unit_to_metres` |
| A05 | AutoCAD | `contours.path`, `surface.path`, `aerial.path`, `linework.path`, `records.layers[].path` | all | Each part's file, by pointer only | yes: `layout.contours`, `layout.landxml`, `layout.imagery_drape`, `layout.linework_dxf`, `layout.parcels_gpkg`, `layout.zoning_gpkg`, `layout.hydrography_gpkg` (and the DXF twins) |
| A06 | AutoCAD | `records.layers[].name` | all | The drawing layer name of each public-record layer | partly: `vector.layers[].name` for vector layers; the record layers are named by their `layout` key |
| A07 | AutoCAD | `contours.sha256`, `surface.sha256`, `aerial.sha256`, `linework.sha256`, `records.layers[].sha256` | all | Every file is verified before anything is drawn | partly: as R12 |
| A08 | AutoCAD | `surface.format`, `aerial.format`, and the same on a record or linework pointer | all | LandXML builds a TIN surface and a PNG is the aerial; any other format is skipped by name | partly: the `layout` key and the file's type say it; no `format` member on the neutral pointers |
| A09 | AutoCAD | `contours.horizontal_frame`, `surface.horizontal_frame`, `aerial.horizontal_frame`, `linework.horizontal_frame`, `records.layers[].horizontal_frame` | all | Absolute or local per file; a file not shown to be in the block's frame is skipped by name | partly: `frame.coordinate_mode` |
| A10 | AutoCAD | `contours.horizontal_units`, `.vertical_units`, `surface.horizontal_units`, `.vertical_units`, and the same on every pointer | all | The units each file's coordinates and heights are in | partly: `frame.unit_to_metres`, `.vertical_unit_to_metres` |
| A11 | AutoCAD | `contours.vertical_reference`, `surface.vertical_reference` | all | Heights placed as absolute only when stated absolute; otherwise the part is skipped by name | partly: as R15 |
| A12 | AutoCAD | `contours.vertical_datum`, `surface.vertical_datum` | all | Read with the pointer; no decision uses it today | yes: `elevation.contours.vertical_datum`, `elevation.landxml.vertical_datum`, `frame.vertical_crs` |
| A13 | AutoCAD | `aerial.width`, `.height` | all | The image's pixel size, read with the pointer; no decision uses it today | none on the drape: `imagery.drape` has no pixel size; `landcover.*` rasters do |
| A14 | AutoCAD | `aerial.insertion.x`, `.y`, `.width`, `.height`, `.rotation_deg` | all | Where the aerial is inserted in the drawing, its size in the frame's unit and its rotation; applied as stated | none: `imagery.drape.extent` is a rectangle in `extent_crs`, with no rotation and not in the file frame's unit |
| A15 | AutoCAD | `readiness.contours`, `.surface`, `.aerial`, `.records`, `.linework`, each `.present` and `.reason`; `readiness.surface.requires` | all | Whether a part was produced; the reason is shown, and `requires` names the Civil 3D part a surface needs | none, as R10 |

### Unreal, the native import

| Row | Host | Member read | Read when | Use | Neutral 2.x |
|---|---|---|---|---|---|
| U01 | Unreal | `heightmap.path`, `.sha256` | all | The landscape's heightmap, found by pointer and verified | partly: `layout.heightmap`; `files[<path>].sha256` on a bundle stored as parts |
| U02 | Unreal | `heightmap.resolution` | all | The heightmap's side in pixels; the landscape is refused when it disagrees with the component count and the quads per component | none |
| U03 | Unreal | `heightmap.row0_is_north` | all | Which edge row 0 is, so the heightmap is not flipped | yes: `frame.row_order` |
| U04 | Unreal | `heightmap.uint16_mapping.z_min_m`, `.z_max_m` | 1.x | What the 16-bit pixels mean in metres; the 2.0 reader falls back to the file's own mapping | yes: `value_mapping.min`, `.max` on the heightmap's `file_entries` entry |
| U05 | Unreal | `heightmap.landscape_transform.scale_x_percent`, `.scale_y_percent`, `.z_scale_percent`, `.location_z_offset_cm`, `.section_size_quads`, `.sections_per_component`, `.component_count_x`, `.component_count_y` | all | The landscape's scale, vertical offset and component layout, applied as stated (the engine's own units) | none |
| U06 | Unreal | `imagery_drape.source`, `.sha256` | all | The drape's image, in the fixed metric frame, found by pointer and verified | partly: `imagery.drape.source`, `.sha256`; the neutral drape can be in another frame on an order that is not in the fixed one |
| U07 | Unreal | `imagery_drape.extent` (left, bottom, right, top) | all | The ground rectangle the drape covers | yes: `imagery.drape.extent`, with `extent_crs` |
| U08 | Unreal | `imagery_drape.alignment` | all | The bundle's claim that the drape and the heightmap's rectangle match; a claim the two rectangles refute is refused | none |
| U09 | Unreal | `mesh_alternative.path`, `.up_axis`, `.sha256` | all | The terrain mesh, its vertical axis (any other than the one understood is refused) and its digest | partly: `layout.mesh_formats` names the files; `frame.axis_mapping` states the axes; `files[<path>].sha256` |
| U10 | Unreal | `mesh_alternative.nanite_recommended` | all | Whether the terrain mesh is built with Nanite | none |
| U11 | Unreal | `readiness.mesh_import.reason` | all | The stated reason the mesh was not produced, shown when there is nothing to import | none, as R10 |
| U12 | Unreal | `buildings_mesh.path`, `.up_axis` | all | The buildings' mesh and its vertical axis | partly: `layout.buildings` (and `buildings.formats[]`); `frame.axis_mapping` |
| U13 | Unreal | `landscape_layers.<layer>.path`, `.sha256`, `.materials`, for each of `material_weights`, `water_mask`, `ndvi`, `slope`, `aspect`, `worldcover`, `canopy_height`, `hillshade` | all | The coverage rasters that become paint layers; the material names are applied verbatim, a paint layer's name included | partly: `layout.<layer>` for the files; `value_mapping.bands`, `.classes` state what the pixels mean, and whether they carry the same material names is not checked here |
| U14 | Unreal | `landscape_layers.<layer>.ue_ready[].path`, `.sha256`, `.encoding`, `.width`, `.height`, `.size_bytes` | all | The engine-ready rasters (one per plane), read as stated | partly: `value_mapping.encoding`; `landcover.<layer>.width`, `.height`; `files[<path>].size_bytes` |
| U15 | Unreal | `landscape_layers.<layer>.ue_ready[].value_mapping.min`, `.max`, `.to_value`, `.units`, `.value_units`, `.nodata_value`, `.true_value`, `.false_value`, `.identity`, `.bands`, `.classes` | 1.x | What a pixel means; where the block states none the reader takes the file's own | yes: `value_mapping` on the file's `file_entries` entry (`min`, `max`, `to_value`, `value_units`, `nodata_value`, `true_value`, `false_value`, `identity`, `bands`, `classes`, `encoding`) |
| U16 | Unreal | `foliage_points.path`, `.sha256`, `.point_count` | all | The tree points, verified, with the row count checked when stated | partly: `layout.tree_points`, `landcover.tree_points.point_count`; the neutral file is in the delivery frame, the block's in the fixed one |
| U17 | Unreal | `foliage_points.crs`, `.units`, `.horizontal_units` | all | The file's frame, judged by the logic unit, which refuses the layer by name when it is not the host's | yes: `landcover.tree_points.crs`, `.units`, `.horizontal_units`, and `frame` |
| U18 | Unreal | `foliage_points.levels`: for MAX, MED and MIN, `same_as`; `cut.rows`; a pointer level's `path`, `sha256`, `point_count`, `crs`, `units`, `horizontal_units` | all | Which tree level the project asks for and what the file at that level is | yes: `landcover.tree_points.levels` |
| U19 | Unreal | `road_splines.path`, `.sha256`, `.vertical_datum`, `.tables` | all | The host's own GeoPackage of road centrelines, preferred over the shared layer; one table per layer | partly: `vector.layers[name=road_splines].formats[].path`, `.sha256`, `.tables`, `.vertical_datum`; no neutral member names a copy in a frame other than the order's `-Projected` twin |
| U20 | Unreal | `georeference.crs_projected` | all | The CRS of the fixed metric frame, compared with a file's stated CRS before the file is placed; its EPSG code goes to the road reader | partly: `delivery.local_origin.utm_epsg`; `location.site_origin.projected.crs` |
| U21 | Unreal | `georeference.origin.utm.easting_m`, `.northing_m`, `.epsg` | all | The AOI origin every placed point is subtracted from; a bundle that states none places nothing by it | partly: `delivery.local_origin.easting_m`, `.northing_m`, `.utm_epsg`; `location.site_origin.projected.x`, `.y` is the same point in the delivery CRS, not necessarily the UTM one |
| U22 | Unreal | `georeference.origin.ground_orthometric_h_m` | all | Read into the manifest facts; no decision uses it today | yes: `location.site_origin.datum_height_m` |
| U23 | Unreal | `georeference.planet_shape` | all | Fail-closed: anything but the flat shape is refused | none |

### Unreal, the Cesium stream (tiles mode)

"Stream into Cesium" reads the manifest through the same parse as the native import and then adds
these. It places no landscape and imports no asset.

| Row | Host | Member read | Read when | Use | Neutral 2.x |
|---|---|---|---|---|---|
| T01 | Unreal | `layout.cesium_terrain` (outside the block) | all | The tileset's directory in the archive, the only entries extracted for the stream | yes: `layout.cesium_terrain`, `cesium_terrain.layer_json` |
| T02 | Unreal | `cesium_terrain.tile_count` (outside the block) | all | The tile count shown with the stream | yes: `cesium_terrain.tile_count` |
| T03 | Unreal | `bbox.west`, `.south`, `.east`, `.north` (outside the block) | all | The AOI rectangle in degrees, read into the stream's report | yes: `bbox` |
| T04 | Unreal | `imagery_drape.source`, `.sha256`, `.extent` | all | The one drape the stream serves, extracted when present and valid | partly: as U06 and U07 |
| T05 | Unreal | `georeference.origin.lon`, `.lat`, `.ground_orthometric_h_m` | all | The Cesium georeference's origin longitude and latitude, set from the bundle at each stream; a manifest the helper cannot read leaves the level's origin untouched. The height is returned and not used | yes: `location.site_origin.longitude`, `.latitude`, `.datum_height_m` |

### Read outside the host block, because a download view depends on it

A host download view holds only the files in that host's block. When the order delivered a file the
plugin reads that the view lacks, the plugin fetches the whole bundle instead of importing from a
view that would leave the layer out silently. The view request names the host and asks every entry at
its most complete level. These tokens are the files that
check is made on; each is a file a host reads that no host block points at.

| Row | Host | Token (file read outside the block) | Use | Neutral 2.x |
|---|---|---|---|---|
| O01 | Revit | `landcover.tree_points_csv`, through `layout.tree_points` and `landcover.tree_points` (the frame, units, count and levels are neutral reads) | The tree points, placed as families; without them the import would run with the trees missing | yes: `layout.tree_points`, `landcover.tree_points` |
| O02 | AutoCAD | none | Its import reads the block's pointers and nothing else, so a view is always enough | not applicable |
| O03 | Unreal | `flood.nfhl`, through `layout.flood_zones` | The flood records' GeoPackage, verified and credited with the public records | yes: `layout.flood_zones`, `flood.nfhl` |
| O04 | Unreal | `cesium.terrain`, through `layout.cesium_terrain` | The quantized-mesh tileset the stream serves (T01) | yes: `layout.cesium_terrain` |

In every `O` row the neutral member already exists. What 3.0 has to settle is how a host's view is
cut when no block lists the files that host reads.

### Declared in a block and read by no plugin

Listed so that a row is not assumed missing. Each is a candidate to drop in 3.0, which is the format
text's decision.

- `hosts.unreal`: `engine_min_version`, `units_note`, `readiness.landscape`, `readiness.mesh_terrain`,
  `file_frame`, `heightmap.format`, `.native_gsd_m`, `.landscape_post_spacing_m`, `.levels`,
  `georeference.crs_geographic`, `.is_projected`, `.origin.lon` and `.lat` for the native import,
  `imagery_drape.format`, `.derive_in_plugin`, `.effective_gsd_m`, `.extent_crs`, `.levels`,
  `mesh_alternative.note`, `.levels`, `buildings_mesh.note`, `.levels`, `foliage_points.columns`,
  `.uncapped_point_count`, `.point_cap`, `.rank`, and on `road_splines` `.format`, `.crs`, `.z_unit`,
  `.feature_count`, `.size_bytes`.
- `hosts.revit`: `host_min_version`, `units_note`, `georeference.crs_geographic`, `.is_projected`,
  `toposurface_points.format`, `.point_count`, `surface_dxf.surf_type`, `.triangle_count`,
  `.decimated_from_triangle_count`, `ifc_site.schema`, `.map_conversion_scale`,
  `.terrain_is_context_geometry`, `.footprint_count`, `drape.width`, `.height`, `.effective_gsd_m`.
  Revit does read `surf_type`, `schema`, `triangle_count`, `footprint_count` and `point_count`, but
  from the neutral entries (`elevation.*`, `buildings.ifc`, `landcover.tree_points`), never from its
  block.
- `hosts.autocad`: `host_min_version`.

### Not covered

The Blender add-on is not in this table: the ticket names the Unreal, Revit and AutoCAD plugins.
