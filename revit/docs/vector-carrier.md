# The Revit vector carrier, and where a linked site IFC lands

The record of a spike: which shared file the Revit plugin should read its vector layers from, so the
Revit-only GeoJSON in `hosts.revit.vectors` can go, and whether Revit places a linked site IFC by its
`IfcMapConversion`. The prototypes and the probe that produced every number here are in
[`tools/carrier-spike/`](../tools/carrier-spike/README.md); nothing in the plugin changed.

## The decision

**A GeoPackage in the order's CRS.** Both candidates read every layer with every attribute and
placed every vertex where today's GeoJSON places it, so placement did not decide it. The GeoPackage
won on what each format can say for itself: it carries its CRS, so a file in the wrong frame is
refused with a reason (`HPS-53`) rather than placed; it holds polygons with holes, per-vertex Z and
typed attributes natively, where the DXF needs three private conventions no other DXF reader knows;
it read faster and is a quarter the size; and its dependency costs nothing to ship. It is also a file
QGIS, ArcGIS and Civil 3D open as they are, which the attributed DXF is only partly.

## The carrier, exactly

What the pipeline emits for the Revit block, and what the reader takes:

- **File:** one GeoPackage 1.3 file (OGC 12-128r19), its path in the manifest's Revit vectors block, as
  every Revit path is (`HPS-32`). The bundle already ships one beside the GeoJSON,
  `Site/Vector/SiteVectors.gpkg`, in this frame; the carrier is that file with the table and column
  names below.
- **CRS:** the origin's own, `hosts.revit.georeference.origin.projected.epsg`, registered in
  `gpkg_spatial_ref_sys` as `organization = 'EPSG'` and `organization_coordsys_id = <that code>`.
  Coordinates are **absolute** eastings and northings in that CRS's linear unit, which is the origin's
  `linear_unit` (`ftUS` on the State Plane tier). On a tier whose files are local offsets today, the
  carrier is still written absolute in the origin's CRS and unit: a GeoPackage has no honest way to
  say "offsets about a point", and the reader places absolute coordinates by subtraction alone.
- **Z:** `road_splines` only, as XYZ: absolute orthometric height in the same linear unit, on the
  vertical datum the Revit block names. Every other table is XY.
- **Tables:** one per layer, named as the manifest names the layer, geometry column `geom`, primary
  key `fid`. A ring list is exterior first, as WKB has it; orientation is not read. Spatial index
  optional and ignored.

| table | geometry | the columns the importer reads |
|---|---|---|
| `road_splines` | `LINESTRING Z` or `MULTILINESTRING Z` | `name`, `class`, `width_m_estimated`, `subtype` |
| `road_polygons` | `MULTIPOLYGON` | `class`, `width_m_estimated`, `lowest_level` |
| `water` | `MULTIPOLYGON` | `name`, `class`, `subtype` |
| `water_line` | `MULTILINESTRING` | `name`, `class`, `subtype` (not drawn by Revit today; kept for other readers) |
| `land_use` | `MULTIPOLYGON` | `name`, `class`, `subtype` |
| `land_cover` | `MULTIPOLYGON` | `subtype` |
| `flood_zones` | `MULTIPOLYGON` | `fld_zone`, `zone_subty` |
| `steep_slope` | `MULTIPOLYGON` | `threshold_deg` |
| `building_footprints` | `MULTIPOLYGON` | `name`, `class`, `subtype` |

- **Columns:** `name`, `class`, `subtype`, `fld_zone`, `zone_subty` and `lowest_level` are `TEXT`;
  `width_m_estimated` (metres, whatever the CRS unit) and `threshold_deg` are `REAL`. A table carries
  the columns its layer has; a missing column reads as empty, a `NULL` as absent, and any other column
  (`overture_id`, `z_unit`, ...) is ignored. `lowest_level` is the fidelity cut field, as today.
- **One table per geometry family.** Today's `Site/Vector/Water.geojson` mixes lines and polygons;
  the prototype read a mixed `GEOMETRY` table without trouble, but a mixed table is the one thing
  ArcGIS will not show, and the importer draws water from polygons only, so water splits in two.

## How the two compared

One bundle, a 4.8 km by 4.8 km State Plane order (`EPSG:6605`, US survey feet, NAVD88): 272 road
centrelines (17,237 vertices, with Z), 8 road-surface polygons (202 rings), 30 water features, 26
land-use and 10 land-cover polygons, 13 steep-ground polygons, and the order's 124 building
footprints. Both carriers were written from the bundle's own Revit GeoJSON by
[`make_carriers.py`](../tools/carrier-spike/make_carriers.py), the footprints reprojected into the order's
CRS by GDAL (no Revit GeoJSON carries footprints today, ADR 0012).

| | GeoJSON (today) | GeoPackage | DXF with extended data |
|---|---|---|---|
| Features read, of today's | baseline | 564 of 564 lines and rings, 124 of 124 footprints | 564 of 564, 124 of 124 |
| Worst vertex delta against GeoJSON | baseline | 0 m plan, 0 m Z, over 25,719 vertices | 0 m plan, 0 m Z, over 25,719 vertices |
| Attributes (the eight the importer reads) | baseline | all eight on every feature; `threshold_deg` reads `35` where GeoJSON wrote `35.0` (same number) | all eight, text verbatim |
| Placement in Revit (bounding boxes, every layer) | baseline | identical to 0.001 ft | identical to 0.001 ft |
| Cold read of every layer inside Revit 2027 | 77.5 ms | 64.2 ms, `winsqlite3.dll` load included | 894.4 ms (the prototype walks the whole file once per layer; one walk is 96 to 194 ms) |
| Placing every layer as DirectShape lines, Revit 2027 | 815 ms | 1,041 ms | 777 ms |
| Size on disk, every layer | 1.05 MB (no footprints) | 0.87 MB (with footprints) | 3.53 MB |
| CRS in the file | a `crs` member the reader does not read | yes: read, and a mismatch refused | none: the manifest's word alone |
| Holes and Z | native | native | by convention: `ring=outer/hole` and `polygon=N` in extended data, Z only on a 3-D `POLYLINE` |
| Attributes | native, typed | native, typed | by convention: `key=value` strings under the `MANTLEPLACE` application |
| What the reader needs | `System.Text.Json` | SQLite: `winsqlite3.dll`, part of Windows since 10 (1607), by P/Invoke | nothing: a pure group-code walk |
| What the writer needs | GDAL | GDAL (`ogr2ogr -f GPKG`) | ezdxf: GDAL's DXF driver writes no attribute |

The placement time is the same work in each mode (the same lines into the same project) and the
spread between them is run-to-run noise; the read is the only step the carrier changes. In every run
the first vertex of the first road read back in Revit's shared coordinates at exactly the easting and
northing the file wrote, `(2573184.697, 461982.082)` survey feet.

### The dependency, decided

The SQLite a GeoPackage needs does **not** need a NuGet package. `revit/Directory.Build.props` forbids
one, and `Microsoft.Data.Sqlite` would bring three, plus a native `e_sqlite3.dll` beside the add-in
that another add-in's copy can shadow in Revit's process. The prototype calls the `winsqlite3.dll`
Windows ships, by P/Invoke: seventeen entry points, no file shipped, and it loaded in Revit's own
process in both runtimes Revit runs (SQLite 3.51.1 in Revit 2027 on .NET 10 and in Revit 2025 on
.NET 8, where every layer read cold in 72.5 ms). The cost is that the SQLite half of the reader runs only on Windows: the blob and WKB
decoder is pure and belongs in `Core`, tested on every runner, and the SQLite calls belong in
`Client`, where the Windows suite covers them.

### What the DXF would have cost

None of it is fatal, all of it is a convention this repository would own alone: attributes as
extended data that AutoCAD keeps and shows nobody; an outer ring and its holes as separate closed
`LWPOLYLINE`s tied together by a number; a road's Z only on a 3-D `POLYLINE` and its `VERTEX` run,
because an `LWPOLYLINE` has one elevation; and a CRS the file cannot state, so a DXF in the wrong
frame would be placed rather than refused. The bundle's `Drawings/Linework.dxf` shows the default
path: GDAL-free, written by ezdxf, absolute in the order's CRS, and carrying no attribute at all.

## The linked site IFC

**Revit honours `IfcMapConversion`, in 2025, 2026 and 2027, and the plugin's link already lands in
the right place.** The bundle's `Site/Site.ifc` is written in offsets about the origin
(`horizontal_frame: local_enu`), with an `IfcMapConversion` naming the origin's easting and
northing, scale 1 and no rotation, against an `IfcProjectedCRS` of `EPSG:6605`. Converted at
reference intent, as the plugin converts it, the model's project position at its internal origin read
back as E/W 2573308.269, N/S 464289.085 survey feet and angle 0, which is the map conversion verbatim,
and its site location as the origin's latitude and longitude, in all three versions (12.5 to 17.8 s
to convert in 2025 and 2027, 118.6 s in 2026).

Linked into a fresh project whose survey point the probe had set as the plugin sets it, the link
transform was the identity, origin to origin (the plugin's placement) and by shared coordinates
alike, because the two models then share one coordinate system. Each of the 124 linked buildings,
matched to the nearest GeoPackage footprint centre to centre in plan, sat a median 0.003 ft away
(90th percentile 0.005 ft); 120 of 124 lie within 0.1 ft, and the other four, up to 15.8 ft out, are
four buildings whose IFC solid and Overture footprint do not share a centre, which this run did not
go on to explain. The project's shared coordinate system was unchanged by every link.

**What it needs, then, is what the plugin already does:** set the survey point from the manifest and
link origin to origin. Placing by shared coordinates (`ImportPlacement.Shared`) lands in the same
place but is not safe to adopt: in Revit 2027 alone it makes the host take on the link's coordinate
reference system, raises "The survey point is outside the current coordinate reference system", and
moves the host's site location to latitude 43.586, longitude -97.438, the point the survey point's
shared origin falls on in `EPSG:6605`, some 800 km from the site. Origin to origin raised nothing in
any version, and the site location the plugin set stayed put. The same `IfcMapConversion` is what an
IFC carries for any other host, so a `Model.ifc` written the same way needs nothing more from Revit.

## What is left for the work that follows

- **The ETL ticket** emits the carrier above: rename the tables of the GeoPackage it already writes,
  split water, add `building_footprints`, and point the Revit vectors block at it.
- **The plugin** swaps `SiteVectorReader` for the GeoPackage reader behind the same `SiteFeature`
  list: the decoder into `Core`, the SQLite calls into `Client`, the parity test kept. `threshold_deg`
  is a `REAL`, so the steep-ground label formats the number itself rather than echoing the file's text.
- The GeoJSON block goes when no supported manifest version publishes it.
