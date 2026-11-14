<!-- SPDX-License-Identifier: CC-BY-4.0 -->
<!-- SPDX-FileCopyrightText: Mantle Place LLC -->

# The file-by-host table

Before the 3.x text was frozen, three bundles (of 0.25, 2 and 64 km²) were repacked into the 3.x tree ([`format.md`](format.md) §1), and every file was opened in eight hosts, each with its own importer and no Mantle Place plugin. This page publishes the result. It is evidence for the tree, not part of the contract: an importer can change in a host's next release, and the promise in [`format.md`](format.md) §3 does not.

**The rule the run applied.** A data file passes when at least one host opens it without conversion; a file no host opens is cut, and an input today's plugins need that no file holds is added. `Readme.md` and `Manifest.json` are exempt, since no host has an importer for them. No file was cut by this rule.

**The hosts.** QGIS 3.34.12, Blender 5.2.0 LTS, 3ds Max 2027.2, AutoCAD 2027, Civil 3D 2027, Revit 2027, Unreal Engine 5.8.3 (the Landscape import for `Elevation.png`) and Twinmotion 2026.2. Unreal Engine and Twinmotion were run on the host-ready formats only (glb, usdz, obj, png, csv).

**What the rows are.** Every file of the 0.25 km² Max download as it was repacked: sixty files. Nine files of the frozen tree are not in it, because they joined the tree on the run's findings or had no source in the three bundles:

- `Basemap/Pavement.gpkg` and `.dxf` and `Records/Habitat.gpkg` and `.dxf` were added because today's plugins read them and no other file holds them;
- `Landcover/Landcover.dxf` and `Elevation/Slope.dxf` were added so that every vector layer is a pair; each is written by the same writer as the other DXFs of the tree;
- `Elevation/Terrain.mtl` was added so that `Terrain.obj` opens textured;
- the `Records/Parcels` pair is in the tree, but none of the three bundles had a parcel source; `Records/Zoning`, written by the same writer in the same shape, stands in for it.

**Cells:** `ok` opened with no warning; `warn` opened with a warning; `FAIL` the host's import failed; `none` the host has no importer for the format; `-` not run.

| File | QGIS | Blender | 3ds Max | AutoCAD | Civil 3D | Revit | Unreal | Twinmotion | Opens clean in |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `Manifest.json` | none | none | none | none | none | none | - | - | 0 |
| `Readme.md` | none | none | none | none | none | none | - | - | 0 |
| `Elevation/Aspect.tif` | ok | ok | FAIL | FAIL | FAIL | FAIL | - | - | 2 |
| `Elevation/Contours.dxf` | warn | none | ok | ok | ok | warn | - | - | 3 |
| `Elevation/Elevation.png` | warn | ok | ok | ok | ok | ok | ok | ok | 7 |
| `Elevation/Elevation.tif` | ok | ok | FAIL | FAIL | ok | ok | - | - | 4 |
| `Elevation/Hillshade.tif` | ok | ok | FAIL | ok | ok | ok | - | - | 5 |
| `Elevation/Slope.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Elevation/Slope.tif` | ok | ok | FAIL | FAIL | FAIL | FAIL | - | - | 2 |
| `Elevation/Terrain.dxf` | warn | none | ok | ok | ok | ok | - | - | 4 |
| `Elevation/Terrain.glb` | none | ok | none | none | none | none | ok | ok | 3 |
| `Elevation/Terrain.landxml` | none | none | FAIL | none | ok | none | - | - | 1 |
| `Elevation/Terrain.obj` | none | warn | warn | none | none | ok | warn | ok | 2 |
| `Imagery/Imagery.png` | warn | ok | ok | ok | ok | ok | ok | ok | 7 |
| `Imagery/Imagery.tif` | ok | ok | FAIL | ok | ok | ok | - | - | 5 |
| `Landcover/Canopy.tif` | ok | ok | FAIL | ok | ok | ok | - | - | 5 |
| `Landcover/Landcover.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Landcover/Landcover.tif` | ok | ok | FAIL | ok | ok | ok | - | - | 5 |
| `Landcover/Trees.csv` | ok | none | none | none | FAIL | none | FAIL | none | 1 |
| `Landcover/Trees.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Materials/Built.png` | warn | ok | ok | ok | ok | ok | ok | ok | 7 |
| `Materials/Dirt.png` | warn | ok | ok | ok | ok | ok | ok | ok | 7 |
| `Materials/Forest.png` | warn | ok | ok | ok | ok | ok | ok | ok | 7 |
| `Materials/Grass.png` | warn | ok | ok | ok | ok | ok | ok | ok | 7 |
| `Materials/Rock.png` | warn | ok | ok | ok | ok | ok | ok | ok | 7 |
| `Materials/Sand.png` | warn | ok | ok | ok | ok | ok | ok | ok | 7 |
| `Materials/Snow.png` | warn | ok | ok | ok | ok | ok | ok | ok | 7 |
| `Materials/Water.png` | warn | ok | ok | ok | ok | ok | ok | ok | 7 |
| `Basemap/Basemap.png` | warn | ok | ok | ok | ok | ok | ok | ok | 7 |
| `Basemap/Basemap.tif` | ok | ok | FAIL | ok | ok | ok | - | - | 5 |
| `Basemap/Basemap.usdz` | none | ok | ok | none | none | none | ok | none | 3 |
| `Basemap/Buildings.dxf` | warn | none | FAIL | ok | ok | ok | - | - | 3 |
| `Basemap/Buildings.glb` | none | ok | none | none | none | none | warn | ok | 2 |
| `Basemap/Buildings.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Basemap/Landuse.dxf` | warn | none | ok | ok | ok | ok | - | - | 4 |
| `Basemap/Landuse.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Basemap/Roads.csv` | ok | none | none | none | none | none | FAIL | none | 1 |
| `Basemap/Roads.dxf` | warn | none | ok | ok | ok | ok | - | - | 4 |
| `Basemap/Roads.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Basemap/Water.dxf` | warn | none | ok | ok | ok | ok | - | - | 4 |
| `Basemap/Water.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Records/Conservation.dxf` | warn | none | ok | ok | ok | ok | - | - | 4 |
| `Records/Conservation.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Records/Contamination.dxf` | warn | none | FAIL | ok | ok | FAIL | - | - | 2 |
| `Records/Contamination.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Records/Floodplain.dxf` | warn | none | FAIL | ok | ok | ok | - | - | 3 |
| `Records/Floodplain.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Records/Heritage.dxf` | warn | none | FAIL | ok | ok | FAIL | - | - | 2 |
| `Records/Heritage.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Records/Hydrography.dxf` | warn | none | ok | ok | ok | ok | - | - | 4 |
| `Records/Hydrography.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Records/Soils.dxf` | warn | none | ok | ok | ok | warn | - | - | 3 |
| `Records/Soils.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Records/Wetlands.dxf` | warn | none | ok | ok | ok | ok | - | - | 4 |
| `Records/Wetlands.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Records/Zoning.dxf` | warn | none | ok | ok | ok | ok | - | - | 4 |
| `Records/Zoning.gpkg` | ok | none | none | none | none | none | - | - | 1 |
| `Site/Site.dxf` | warn | none | ok | ok | ok | warn | - | - | 3 |
| `Site/Site.ifc` | none | none | none | none | none | warn | - | - | 0 |
| `Site/Site.usdz` | none | ok | ok | none | none | none | ok | none | 3 |

## Reading the table

- **Opens clean in no host:** `Readme.md` and `Manifest.json` (text, exempt) and `Site/Site.ifc`, which Revit opens with warnings (IFC4 is partly supported, and the survey point is outside the CRS). **In exactly one:** every GeoPackage (QGIS), `Terrain.landxml` (Civil 3D), and `Roads.csv` and `Trees.csv` (QGIS).
- **QGIS warns on every DXF and every PNG, as expected:** a DXF carries no CRS, so QGIS places it in the project's CRS, and a PNG is a host-ready file in the local site frame with no georeference, as [`format.md`](format.md) §4 has it.
- **At 64 km² two cells change:** `Site/Site.ifc` fails in Revit (the import had not finished after 65 minutes), and `Site/Site.usdz` warns in 3ds Max (terrain vertices that belong to no face). No size limit was met in any other host at the largest size.
- **An Imperial DXF** (`Records/Zoning.dxf` in EPSG:2277, US survey feet) opens in AutoCAD, Civil 3D and Revit, which all read it in US survey feet; QGIS warns (no CRS), 3ds Max fails (it imports hatches only) and Blender has no DXF importer.
- **Med and Min** of the host-ready files were opened in Unreal Engine and Twinmotion, and every cell equals the Max one.

**What the hosts' importers do with the formats:**

- GeoPackage opens in QGIS only; Civil 3D's map import was not run.
- glTF opens in Blender, Unreal Engine and Twinmotion; USDZ in Blender, 3ds Max and Unreal Engine, and Twinmotion has no USDZ importer.
- LandXML opens in Civil 3D only; 3ds Max refuses the `.landxml` extension and opens the same bytes named `.xml`.
- IFC opens in Revit only.
- Both CSVs open in QGIS as points, and in no real-time host without a plugin: Unreal Engine needs a data-table row structure, and Twinmotion has no CSV importer.
- 3ds Max reads no tiled TIFF, and every georeferenced raster is tiled; each one's PNG opens there.
- The 16-bit `Slope.tif` and `Aspect.tif` are refused as images by AutoCAD, Civil 3D, Revit and 3ds Max, and open in QGIS and Blender. The 32-bit `Elevation.tif` stops AutoCAD as an image, while Civil 3D builds a surface from it.
- Unreal Engine imports every PNG as an interface texture by default.

**Findings that became requirements of the tree** ([`conformance.md`](conformance.md) §7), each with no change to the file list:

- `Terrain.obj` stood on its side in Blender and 3ds Max and came in at a hundredth of its size in Unreal Engine and Twinmotion. It is written Y-up, and the Readme states its unit.
- The four Basemap DXFs were in longitude and latitude with no unit, so Revit placed them at a scale no unit gives and 3ds Max imported nothing from `Buildings.dxf`. Every DXF is written in the order's CRS and units.
- `Imagery.tif`'s fourth band, near-infrared, was declared alpha, so a host read it as transparency. It is written as near-infrared.
- A DXF written by a general-purpose writer carried no attributes. Every DXF is written by the producer's writer, with its attributes.

**Everything today's plugins import can be rebuilt from the tree.** Each file the Unreal, Revit and AutoCAD plugins import from a 2.x bundle is in the tree under a name, or derived from a file that stays in one step ([`format.md`](format.md) §12). The terrain tiles the Unreal plugin streams are an Extra beside the bundle.
