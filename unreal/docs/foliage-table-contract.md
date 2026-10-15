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
