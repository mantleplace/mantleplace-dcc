# The tree-points table: its column contract

An import of a bundle that ships `unreal.foliage_points` makes one data table,
`<ContentRoot>/<identity>/Landcover/DT_TreePoints`, with one row per point named `Tree_<index>`
(both from `MantlePlaceImportNaming`). Its row type is `FMantlePlaceTreePointRow`, in the Runtime
module so a cooked build can still load the table.

**The columns are the row struct's property names, and they are the contract.** A PCG graph reads the
table with Load Data Table and binds each attribute by name; a Blueprint reads the same names off a
row. A name changes only by a deliberate edit to this file, the struct and
`FMantlePlaceTreePointsLogic::ColumnContract()` together, and `MantlePlace.Import.TreePointsLogic`
fails when the struct and the contract disagree. A re-import replaces the table
([ADR 0002](../../docs/adr/0002-import-identity.md)), so a changed column is a clean break with no
migration.

## Columns

| Column | Type | Unit and frame | Meaning |
| --- | --- | --- | --- |
| `Position` | vector | engine cm, the Local Projected Frame: North is +X, East is +Y, the AOI centroid is the origin | Where the point is. Z is the ground elevation in cm when `bGroundKnown`, else 0 |
| `FoliageType` | enum `EMantlePlaceFoliageType` | none | What the platform classified the point as: `Tree` or `Shrub` |
| `HeightM` | float | metres, a height above ground | The canopy height as published (the CHM local maximum at the point) |
| `HeightCm` | float | engine cm | `HeightM` in the unit a scatter places in |
| `CrownRadiusM` | float | metres | The crown radius as published |
| `CrownRadiusCm` | float | engine cm | `CrownRadiusM` in the unit a scatter places in |
| `GroundZM` | float | metres, orthometric (EGM2008) | The ground elevation under the point as published; 0 where it was not measured |
| `bGroundKnown` | bool | none | False when the CSV's `ground_z` cell was empty, which the platform writes where the DEM had no data under the point |

### What "decoded" means here

- **`FoliageType` is read, never derived.** The CSV's `foliage_type` column is read only where the
  manifest names the vocabulary its values come from (`landcover.tree_points.foliage_type_vocabulary`,
  MPB 1.2.0); a manifest that names none has no such column and every point is a `Tree`. A value the
  vocabulary does not contain reads as `Tree`, and the vocabulary id itself is not judged, so a value
  this build knows keeps its meaning under a vocabulary it was not taught. Values match exactly.
  Nothing is inferred from `HeightM` or `CrownRadiusM`. The rule is
  [`spec/format.md`](../../spec/format.md) section 4.4, and corpus case
  `manifest.foliageTypeUnknownValue` binds it for both hosts.
- **The centimetre columns are a unit change, not a derived value**, the one `Position` already
  makes. The metre columns stay as the platform published them.
- **`bGroundKnown` exists because 0 is a real elevation.** A graph that snaps trees to the ground
  should do it for the rows where `bGroundKnown` is false rather than plant them at sea level.

## The scatter that binds it

The plugin ships one consumer of the contract: the foliage scatter, opt-in through **Project
Settings → Plugins → Mantle Place → Foliage → Generate a foliage scatter on import**. With it on, an
import builds `<ContentRoot>/<identity>/Landcover/PCG_TreeScatter`, a three-node PCG graph: Load Data
Table on this table, the plugin's **Mantle Place Tree Scatter** node, and a Static Mesh Spawner that
picks each point's mesh by the `Mesh` attribute the scatter node writes. The graph is built in code
at import rather than shipped as an asset, so every order's graph carries that order's own table and
rasters.

The scatter node reads `Position`, `FoliageType`, `HeightCm`, `CrownRadiusCm` and `bGroundKnown` by
these names (`MantlePlaceTreeScatterColumns`, which `MantlePlace.Import.FoliageScatter.Columns` holds
to `ColumnContract()`), and the decoded coverage rasters the same import made:

- **`water_mask` and `worldcover` exclude.** A point on a water texel, or on a land-cover class the
  node's excluded list names (built-up, snow and ice, permanent water by default), is dropped.
- **`canopy_height` modulates trees, not shrubs.** A tree where the canopy reads at or below the
  node's minimum is dropped, the keep chance ramps to one at its full-density height, and a row with
  no `HeightCm` takes the canopy's height. The ramp's heights are node settings, not published
  values; a graph a user edits can change them.
- **The mesh set is the user's.** The Tree meshes and Shrub meshes project settings, defaulting to
  the engine's own basic cone and sphere so the default ships no third-party asset. Each instance is
  scaled to its row's height and crown and stood on the ground: `Position.Z` where `bGroundKnown`,
  else a trace down onto the terrain.

A raster the bundle does not ship filters nothing, and the import log says which. Where it lands and
how a re-import removes it is [`generated-content.md`](generated-content.md).

## Which trees the table holds: the level

The table holds one published level of the tree points, chosen by the project setting
**Tree points level** (Project Settings, Plugins, Mantle Place, Foliage; MAX by default). The bundle
publishes the levels in `foliage_points.levels` and the importer reads the one asked for as stated and
never derives one. A MED or MIN stated as `cut: {rows: N}` takes the first N rows of the one ranked
tree file the bundle ships (a row's number is its rank), checked against the one digest that file has.
A bundle older than MPB 2.0 states them as pointers to a file each, and those are read from that file,
against its own digest, count and frame. `same_as` is followed one hop. A level the bundle does not
publish imports MAX, and the import log says so. `MantlePlace.Import.TreeLevelLogic` covers both shapes.

## What is not a column, because the platform does not publish it

The plugin applies published values and never derives them, so a graph that wants any of these cannot
have it from this table until the manifest carries it. Each is a request against the bundle manifest.

- A per-point density, or a canopy-cover figure: the layer publishes a point per detected crown and
  no density attribute.
- A species, genus or any taxon below `Tree` and `Shrub`. The vocabulary is two values wide.
- A crown base height, a trunk height or a crown shape. The layer publishes one height and one
  radius.
- A foliage-type vocabulary on `unreal.foliage_points` itself. It is read today from
  `landcover.tree_points`, the only place the format publishes it.
- The `truncated_to_tallest` marker on this host's own pointer, which says the platform capped the
  layer. `landcover.tree_points` carries it and this host does not read it (`HPS-33`).

The frame and row-count checks that decide whether the table is made at all are unchanged; see
[`standard-map.md`](standard-map.md) for `HPS-32` and `HPS-53`.
