# The road-spline actor: its property contract

An import of a bundle that ships the shared `road_splines` layer spawns one `AMantlePlaceRoadSpline`
per road line, labelled `MP_RoadSpline_<shortIdentity>_<index>` (from `MantlePlaceImportNaming`). The
class is in the Runtime module, so a saved level that holds one loads in a cooked build.

## Where the roads come from

The heights are placed as the file carries them, and only in the landscape's datum, `EGM2008-orthometric`;
the importer converts none. It reads the Unreal block's own copy first when the manifest publishes one,
`hosts.unreal.road_splines` (MPB 1.16.0; a 2.0 bundle's `-Engine` twin): the same roads draped on the
engine's ground, which a US order needs because its shared layer is `NAVD88 (GEOID18)`. A source that states
another datum, or none, is skipped by name, so a US bundle that publishes no copy places no roads. The import
log says how many splines were created and which file they were read from.

Failing that, the importer reads the layer from its GeoPackage, the file the `gpkg` row of the layer's `formats` in
`vector.layers` points at, through the engine's own SQLiteCore module. The columns it takes are `class`,
`name` and `width_m_estimated`, and the geometry is the lines of the file's feature tables in
EPSG:4326 (a table in another frame is refused by name and never reprojected). A row that lists no
`tables` holds one table, which the importer reads whatever it is named (the producer names it in
CamelCase, `RoadSplines`, not `road_splines`); a file of several tables under such a row is refused
and the layer is read from its `geojson` row, never from one table picked out of the file. A layer with no `gpkg`
row, or one whose GeoPackage cannot be read, is read from its `geojson` row instead: the same roads,
the same properties. Both files are declared payloads, so both are verified before anything is placed.

It replaces a bare actor that carried the road's width, class and name as tag strings. **Nothing in
the plugin reads a road attribute from a tag, and the importer writes none.** The only tag on the
actor is the import tag, `mantleplace_import=<identity>`, which a re-import matches on whatever the
actor's class: a re-import replaces the road actors and never stacks them. There is no migration; a
bare-actor road from an earlier import carries the same tag and goes with them.

## Properties

| Property | Type | Meaning |
| --- | --- | --- |
| `Spline` | `USplineComponent`, the actor's root | The centerline, as world-space points in the Local Projected Frame, heights included |
| `RoadClass` | `FName` | `class` from the layer, verbatim; `None` where the feature carries none |
| `RoadName` | `FString` | `name` from the layer, verbatim; empty where the feature carries none |
| `EstimatedWidthM` | `float`, metres | `width_m_estimated` from the layer; 0 where the feature publishes none |

All are readable from a Blueprint. `RoadClass` is the platform's string and not a vocabulary this
plugin owns, so a graph filters on the values it sees rather than on a list written here.

**Geometry only.** A road mesh or a decal built from the spline is a later feature.

## What is not a property, because the platform does not publish it

Each is a request against the bundle manifest, and none is derived here. The layer publishes a class,
a name and an estimated width per line, and nothing else.

- A lane count, a surface type or a one-way flag.
- A measured width, or a tolerance beside the estimate.
- A stable identifier for a road, so a re-import could tell a road that moved from one that is new.
  The actor's index is its position in the file, which the platform does not promise to keep.
- The source way a line was cut from, and which part of it, where a way was split into the parts of
  a MultiLineString.
