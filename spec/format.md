<!-- SPDX-License-Identifier: CC-BY-4.0 -->
<!-- SPDX-FileCopyrightText: Mantle Place LLC -->

# The MPB format

How a Mantle Place Bundle of the 3.x major is put together, and how a reader is meant to read it: the tree, the names, the formats, the units, the precision, the levels, the manifest and the promise.

A bundle delivered before 3.0 stays at its version and is never repackaged ([`compatibility.md`](compatibility.md) §8). Its text is [`format-2.md`](format-2.md), which a reader of 2.x bundles still needs. Source comments and documents written for 2.x cite sections as "format.md §N"; those numbers name sections of the 2.x text, now `format-2.md`.

**What decides.** The manifest's members, their types and their permitted values belong to the 3.x schema, published in the schema namespace (`https://mantle.place/.well-known/schemas/bundle-manifest/`). This page names the facts the manifest states, not its members. The file table in §6 is normative for the tree: the producer generates its catalog from one registry, and a check fails the build when that registry and this table disagree.

Requirement words (MUST, MUST NOT, SHOULD, MAY) carry their [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119) meanings.

## 1. A bundle is a zip of seven subject folders

```
Readme.md   Manifest.json
Elevation/  Elevation.tif  Elevation.png  Terrain.glb  Terrain.obj  Terrain.mtl  Terrain.dxf  Terrain.landxml
            Slope.tif  Slope.gpkg  Slope.dxf  Aspect.tif  Hillshade.tif  Contours.dxf
Imagery/    Imagery.tif  Imagery.png
Landcover/  Landcover.tif  Landcover.gpkg  Landcover.dxf  Canopy.tif  Trees.gpkg  Trees.csv
Materials/  Water.png  Grass.png  Forest.png  Dirt.png  Rock.png  Sand.png  Snow.png  Built.png
Basemap/    Basemap.tif  Basemap.png  Basemap.usdz  Buildings.glb  Roads.csv
            Buildings, Roads, Pavement, Water, Landuse   (each .gpkg + .dxf)
Records/    Parcels, Zoning, Floodplain, Wetlands, Soils, Heritage, Conservation, Habitat,
            Hydrography, Contamination   (each .gpkg + .dxf)
Site/       Site.usdz  Site.ifc  Site.dxf
```

- **Seven subject folders, no subfolders.** Every file sits directly in one of `Elevation`, `Imagery`, `Landcover`, `Materials`, `Basemap`, `Records` and `Site`.
- **The root holds only the two files about the whole bundle.** `Readme.md` is written for a person: it is generated for each download, it is not part of the contract, and a reader MUST NOT parse it. `Manifest.json` is the machine entry point (§9).
- **A file is held only where the order produced it.** Each `Records` pair is present only where the AOI has that record, as a record always was. The manifest's catalog says which files one bundle holds, and the manifest lists every promised file it does not hold with the reason (§9.6).
- **Extras are not in the tree.** The Site Brief, the plan sheets and the terrain tiles are separate downloads beside the bundle. They are outside this standard, carry no version promise, and the manifest names none of them (§9.9).

## 2. The naming rules

⛔ **Normative: a 3.x producer follows all six.**

1. **One word, first letter capitalised.** No acronyms, hyphens, numbers, spaces or suffixes. The field's own closed compounds stay one word: `Landcover`, `Landuse`, `Hillshade`, `Basemap`.
2. **A name states its subject.** Countable things are plural and continuous things singular, folders included: `Records`, `Materials`, `Roads`, `Trees` and `Buildings` count; `Imagery`, `Water`, `Pavement` and `Elevation` do not.
3. **The same data in another format shares its name** and differs only by extension: `Roads.gpkg` and `Roads.dxf` hold the same features and attributes. Different data never shares a name anywhere in the tree, which is why the terrain mesh is `Terrain` and not `Elevation`.
4. **No name carries a level, a unit, a frame or a CRS.** A file has the same name and path in every download, at every level and on every order. What a name leaves out the manifest states once (§9).
5. **Extensions are lower case** and are the ones the file table uses (§6). Names are ASCII, and no two entries of one archive differ only by case, so a bundle extracts the same on a case-sensitive and a case-insensitive file system.
6. **Writers use the exact case; readers match regardless of case.**

`Readme.md` and `Manifest.json` follow the same rules. `Manifest.json` keeps its name and its place at the root in every later major, so that a reader written for 3.x can open a newer bundle, read its version and refuse it (§11).

## 3. One promise, and the admission rule

**Every file in the tree is promised for the life of 3.x.** A 3.x minor may add a file; no 3.x release removes, moves or renames one, or changes its format, its family (§4) or what its values mean (§7). There is one kind of file: the 2.x split between canonical and courtesy formats is retired.

**The admission rule.** A file joins the tree only if it is a new subject, or a host-ready copy of an existing subject that some host can neither read nor derive. A host derives a file when one step over a file already in the tree makes it (§12 lists such steps); a host never converts a format to read a file it has an importer for. A file that joins does so in a minor release, which the [changelog](changelog.md) records.

**A profile, never an encoding.** MPB defines no file format. Every file is in a format somebody else specified and maintains; MPB adds the tree, the names, the manifest and the rules in this page. A producer or a reader needs a library for each format it touches, and a parser for nothing MPB-specific except the manifest.

**Produced Works and ODbL files.** No file is both. `Site/Site.usdz` and every raster and mesh built from ODbL sources are Produced Works (rasterized, meshed, extruded or baked), which a curator can hand on with a credit line. `Basemap/Basemap.usdz`, the vector layers built from ODbL sources and `Site/Site.dxf` are ODbL files, kept complete, and carry their notice and the licence link inside the file (GeoPackage metadata, the DXF header, USD layer metadata). A file no ODbL source contributes to has no ODbL role. The manifest states each file's role, licence and sources (§9.8), and the Readme lists them for a person. No folder means "ODbL".

## 4. Two families, and units by extension

Every data file belongs to one of two families, and its extension says which.

| Family | Extensions | Coordinates | Unit |
| --- | --- | --- | --- |
| **Georeferenced** | `tif`, `gpkg`, `dxf`, `landxml`, `ifc` | Absolute, in the order's CRS; heights in the order's vertical datum | The order's: Imperial or Metric, as the order resolved it |
| **Host-ready** | `glb`, `usdz`, `obj`, `mtl`, `png`, `csv` | Offsets in the local site frame (below) | Metres, on every order |

The manifest states both frames once (§9.4). No file name carries a unit, and no file carries a second frame.

**The georeferenced frame** is the order's delivery: its projected CRS, its vertical datum and geoid, and its horizontal and vertical linear units. Every georeferenced file is in it, and every DXF among them as well: a DXF in longitude and latitude, or with no unit, does not conform.

**The local site frame** is one flat frame for the whole bundle, in metres:

- its origin is the site origin, one point the manifest states in longitude, latitude and height, and again in the projected CRS whose grid the frame's axes follow;
- x points east and y north along that grid, and z up; a height is metres above the site origin's height, in the vertical datum the manifest names for the frame;
- it is flat by definition: no curvature, no projection and no rotation inside it. The angle between its grid north and true north at the origin is stated once (§9.4), for a host that sets one.

Each host-ready format holds that frame in its own convention, which is fixed here and never assumed by a reader for a format that states its own:

- **glTF** (`.glb`) and **OBJ** (`.obj`) are Y-up: x east, y up, z south.
- **OpenUSD** (`.usdz`) is Z-up, in metres: x east, y north, z up.
- **CSV** (`.csv`) carries `x`, `y` and a height column in the frame's axes: x east, y north, z up.
- **PNG** (`.png`) is a grid over the site with row 0 at its north edge; the manifest states the rectangle it covers in the frame (§9.5).

⛔ **Placement values are pre-derived and MUST be applied verbatim; a reader MUST NOT re-derive them.** The site origin, its height, the frame's grid and datum, each raster's rectangle and each value scale are computed once, by the producer, and published. A reader multiplies published numbers into its host's convention (metres to centimetres, say) and does nothing else. A reader that recomputes a transform "to be safe" has built a second implementation of a pipeline it cannot see, and the two drift in a curator's project rather than in CI.

⛔ **A reader MUST fail closed on a unit, a datum, an axis or a value it does not understand**, naming the value. A linear unit misread by a factor is a site misplaced by a whole ratio.

**No reader projects.** Every georeferenced file is in the order's CRS and every host-ready file in the local site frame, so the 2.x permission to project a layer stated in longitude and latitude has nothing left to apply to. The only geographic values in a 3.x bundle are the site origin and the AOI rectangle in the manifest.

## 5. Formats

- **Vector layers ship as pairs.** Every vector layer is a `.gpkg` and a `.dxf` holding the same features and the same attributes, in 3D where the data has heights (the roads, draped on the ground) and in 2D where it has none. The one exception is the tree points: `Trees` ships as a `.gpkg` and a `.csv`, with no `.dxf`.
- **Every DXF carries its attributes**, stored as extended data on each entity, as the producer's writer stores them. A DXF with geometry and no attributes does not conform.
- **Georeferenced rasters are tiled GeoTIFFs (COG)**, compressed losslessly, with a predictor where it helps.
- **Every host-ready raster is a PNG.**
- **Meshes:** glTF 2.0 binary (`.glb`), OBJ with its material file (`.obj` and `.mtl`), OpenUSD packages (`.usdz`), the DXF 3D faces of the terrain and the LandXML surface of the terrain.
- **The site model** is IFC 4.0.2.1 (IFC4 ADD2 TC1).
- **Tables** are UTF-8 CSV with a header row and a comma separator. ⛔ **A reader MUST resolve the columns by header name, never by position, and MUST ignore a column it does not know.** A minor release may add a column.
- **The foliage type.** The last column of `Trees.csv`, and the attribute of the same name in `Trees.gpkg`, is `foliage_type`, whose closed vocabulary the platform owns and the manifest names. Today it is `tree` and `shrub`. ⛔ **A reader MUST read a value it does not know as `tree`.** A new value is a new vocabulary version, never a change of meaning for an existing one.

## 6. The file table

One row per file of the tree, sixty-nine in all. **Family** is §4's. **Level** says whether the file is built at each level (§8): `yes` means the Med and Min downloads hold a coarser file under the same name; `no` means the three downloads hold the same bytes. **Precision** is the data type, the scale (what one stored unit means) and the no-data value; no reader depends on a scale tag inside the file, because this column and the catalog state it (§7). **Count** is what the catalog counts for that file (§9.5).

| File | Subject | Format | Family | Level | Precision | Count |
| --- | --- | --- | --- | --- | --- | --- |
| `Readme.md` | The bundle, for a person | Markdown | Whole bundle | Per download | Text | None |
| `Manifest.json` | The bundle, for a machine | JSON | Whole bundle | Per download | Text | None |
| `Elevation/Elevation.tif` | Ground elevation | GeoTIFF | Georeferenced | no | Float32; one unit is one vertical unit of the order, in its datum, exact as the source states it; no-data −32768 | Pixels |
| `Elevation/Elevation.png` | Ground elevation, for a heightmap import | PNG, 16-bit grayscale | Host-ready | yes | UInt16; height in the local site frame, in metres, = offset + code × step, both stated in its catalog entry; no no-data code: every pixel holds a height | Pixels |
| `Elevation/Terrain.glb` | The terrain mesh | glTF 2.0 binary | Host-ready | yes | Float32 vertices, metres; Y-up | Triangles |
| `Elevation/Terrain.obj` | The terrain mesh | OBJ | Host-ready | yes | Decimal text, metres; Y-up; texture coordinates on every vertex | Triangles |
| `Elevation/Terrain.mtl` | The terrain mesh's material | OBJ material library | Host-ready | no | Text: one material whose diffuse map is `../Imagery/Imagery.png` | None |
| `Elevation/Terrain.dxf` | The terrain surface | DXF, 3D faces | Georeferenced | no | Double coordinates in the order's unit | Triangles |
| `Elevation/Terrain.landxml` | The terrain surface | LandXML TIN | Georeferenced | no | Decimal text in the order's unit | Triangles |
| `Elevation/Slope.tif` | Slope of the ground | GeoTIFF | Georeferenced | no | UInt16; one unit is a tenth of a degree; no-data 65535 | Pixels |
| `Elevation/Slope.gpkg` | Steep ground, as areas | GeoPackage, 2D polygons | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Elevation/Slope.dxf` | Steep ground, as areas | DXF, 2D polygons | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Elevation/Aspect.tif` | Aspect of the ground | GeoTIFF | Georeferenced | no | UInt16; one unit is a whole degree, clockwise from grid north; no-data 65535 | Pixels |
| `Elevation/Hillshade.tif` | Shaded relief | GeoTIFF | Georeferenced | no | Byte; brightness 1 to 255; no-data 0 | Pixels |
| `Elevation/Contours.dxf` | Contour lines | DXF, 3D polylines | Georeferenced | no | Double coordinates in the order's unit; each line at its height | Features |
| `Imagery/Imagery.tif` | Aerial imagery | GeoTIFF, four bands | Georeferenced | no | Byte ×4: red, green, blue, near-infrared, untouched from the source; no no-data code | Pixels |
| `Imagery/Imagery.png` | Aerial imagery, for a drape | PNG, RGB | Host-ready | yes | Byte ×3: red, green, blue; no no-data code | Pixels |
| `Landcover/Landcover.tif` | Land cover classes | GeoTIFF, paletted | Georeferenced | no | Byte; one code per class, the legend in its catalog entry; no-data 0 | Pixels |
| `Landcover/Landcover.gpkg` | Land cover, as areas | GeoPackage, 2D polygons | Georeferenced | no | Double coordinates in the order's unit; the class as an attribute | Features |
| `Landcover/Landcover.dxf` | Land cover, as areas | DXF, 2D polygons | Georeferenced | no | Double coordinates in the order's unit; the class as an attribute | Features |
| `Landcover/Canopy.tif` | Canopy height | GeoTIFF | Georeferenced | no | Byte; one unit is a whole metre; no-data 255 | Pixels |
| `Landcover/Trees.gpkg` | Tree points | GeoPackage, 3D points | Georeferenced | no | Double coordinates in the order's unit; the tree's attributes as in `Trees.csv` | Features |
| `Landcover/Trees.csv` | Tree points | CSV | Host-ready | yes | Decimal metres to the centimetre | Rows |
| `Materials/Water.png` | Water weight; the water coverage | PNG, 8-bit grayscale | Host-ready | yes | Byte; 0 none to 255 full; no no-data code | Pixels |
| `Materials/Grass.png` | Grass weight | PNG, 8-bit grayscale | Host-ready | yes | Byte; 0 none to 255 full; no no-data code | Pixels |
| `Materials/Forest.png` | Forest weight | PNG, 8-bit grayscale | Host-ready | yes | Byte; 0 none to 255 full; no no-data code | Pixels |
| `Materials/Dirt.png` | Dirt weight | PNG, 8-bit grayscale | Host-ready | yes | Byte; 0 none to 255 full; no no-data code | Pixels |
| `Materials/Rock.png` | Rock weight | PNG, 8-bit grayscale | Host-ready | yes | Byte; 0 none to 255 full; no no-data code | Pixels |
| `Materials/Sand.png` | Sand weight | PNG, 8-bit grayscale | Host-ready | yes | Byte; 0 none to 255 full; no no-data code | Pixels |
| `Materials/Snow.png` | Snow weight | PNG, 8-bit grayscale | Host-ready | yes | Byte; 0 none to 255 full; no no-data code | Pixels |
| `Materials/Built.png` | Built-surface weight | PNG, 8-bit grayscale | Host-ready | yes | Byte; 0 none to 255 full; no no-data code | Pixels |
| `Basemap/Basemap.tif` | The rendered map | GeoTIFF, RGB | Georeferenced | no | Byte ×3: red, green, blue; no no-data code | Pixels |
| `Basemap/Basemap.png` | The rendered map, for a drape | PNG, RGB | Host-ready | yes | Byte ×3: red, green, blue; no no-data code | Pixels |
| `Basemap/Basemap.usdz` | The map's features as curves, points and polygons | OpenUSD package | Host-ready | yes | Float32 coordinates, metres; Z-up | Features |
| `Basemap/Buildings.glb` | Extruded buildings | glTF 2.0 binary | Host-ready | yes | Float32 vertices, metres; Y-up | Triangles |
| `Basemap/Roads.csv` | Road centrelines, one row per vertex | CSV | Host-ready | yes | Decimal metres to the centimetre | Rows |
| `Basemap/Buildings.gpkg` | Building footprints | GeoPackage, 2D polygons | Georeferenced | no | Double coordinates in the order's unit; height as an attribute | Features |
| `Basemap/Buildings.dxf` | Building footprints | DXF, 2D polygons | Georeferenced | no | Double coordinates in the order's unit; height as an attribute | Features |
| `Basemap/Roads.gpkg` | Road centrelines, draped | GeoPackage, 3D lines | Georeferenced | no | Double coordinates in the order's unit and vertical datum | Features |
| `Basemap/Roads.dxf` | Road centrelines, draped | DXF, 3D polylines | Georeferenced | no | Double coordinates in the order's unit and vertical datum | Features |
| `Basemap/Pavement.gpkg` | Road surfaces, as areas | GeoPackage, 2D polygons | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Basemap/Pavement.dxf` | Road surfaces, as areas | DXF, 2D polygons | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Basemap/Water.gpkg` | Water bodies and waterways | GeoPackage, 2D | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Basemap/Water.dxf` | Water bodies and waterways | DXF, 2D | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Basemap/Landuse.gpkg` | Land use, as areas | GeoPackage, 2D polygons | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Basemap/Landuse.dxf` | Land use, as areas | DXF, 2D polygons | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Parcels.gpkg` | Parcel boundaries | GeoPackage | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Parcels.dxf` | Parcel boundaries | DXF | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Zoning.gpkg` | Zoning districts | GeoPackage | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Zoning.dxf` | Zoning districts | DXF | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Floodplain.gpkg` | Flood hazard zones | GeoPackage | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Floodplain.dxf` | Flood hazard zones | DXF | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Wetlands.gpkg` | Wetlands | GeoPackage | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Wetlands.dxf` | Wetlands | DXF | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Soils.gpkg` | Soil map units | GeoPackage | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Soils.dxf` | Soil map units | DXF | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Heritage.gpkg` | Historic places | GeoPackage | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Heritage.dxf` | Historic places | DXF | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Conservation.gpkg` | Protected areas | GeoPackage | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Conservation.dxf` | Protected areas | DXF | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Habitat.gpkg` | Critical habitat | GeoPackage | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Habitat.dxf` | Critical habitat | DXF | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Hydrography.gpkg` | Streams and water features | GeoPackage | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Hydrography.dxf` | Streams and water features | DXF | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Contamination.gpkg` | Contaminated sites | GeoPackage | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Records/Contamination.dxf` | Contaminated sites | DXF | Georeferenced | no | Double coordinates in the order's unit | Features |
| `Site/Site.usdz` | The site as one model: terrain, buildings, roads, water, land use and trees, textured | OpenUSD package | Host-ready | yes | Float32 coordinates, metres; Z-up | Triangles |
| `Site/Site.ifc` | The site model | IFC 4.0.2.1 | Georeferenced | no | Double coordinates in the order's unit, placed by the model's map conversion | Features |
| `Site/Site.dxf` | The flat plan drawing, with its paper-space layouts | DXF | Georeferenced | no | Double coordinates in the order's unit | Features |

**What some rows need said.**

- **`Terrain.obj` is written Y-up, in metres, with texture coordinates,** and names `Terrain.mtl` as its material library. OBJ has no unit field, so the Readme states the unit: a host that reads OBJ in metres imports it at 1, and a host that reads OBJ in centimetres imports it at 100. It is the bundle's one fallback mesh, for tools that read neither glTF, USD nor DXF.
- **`Terrain.mtl` points at the bundle's imagery,** with the relative path `../Imagery/Imagery.png`, so the mesh opens textured and no second copy of the pixels is shipped. The texture coordinates of `Terrain.obj` address the pixels of `Imagery.png` at the same level.
- **`Imagery.tif`'s fourth band is near-infrared.** It is written undeclared, with a band description that says near-infrared, and never declared as alpha, so no host reads it as transparency. NDVI is `(band 4 - band 1) / (band 4 + band 1)` of this file (§12).
- **`Elevation.png` sits on the shared vertical ladder.** Its step is one of the ladder's, and its catalog entry states the step, the height of code 0 and its place on the ladder; two heightmaps at one step share one code lattice, so a reader that tiles several lines them up by their stated places and not by each file's range. At every level its side is a size heightmap hosts accept.
- **The eight `Materials` PNGs are weight maps,** one per surface, over the same grid. They are the paint layers a terrain host blends, named by their files. `Water.png` is also the water coverage: no separate water mask ships.
- **`Trees.csv`** has the columns `x`, `y`, `ground_z`, `height_m`, `crown_radius_m` and `foliage_type`. The platform thins the trees by canopy, water and land cover when it writes the file, and ranks them: the Med and Min files are the first rows of the Max file, so a tree keeps its row at every level. `Trees.gpkg` holds the same trees as the Max `Trees.csv`, with the same attributes, in the georeferenced frame.
- **`Roads.csv`** has one row per vertex, with the columns `road`, `vertex`, `x`, `y`, `z`, `class`, `width_m` and `name`. Rows of one road share its `road` number and are in vertex order.
- **`Pavement`** holds the road surfaces as areas, not centrelines: the platform builds them, and a host never buffers a centreline to make them.
- **`Habitat`** is a critical-habitat designation; like every record, it is present only where the AOI has one. The `Parcels` pair is in the tree although many AOIs have no parcel source.
- **`Basemap.usdz` and `Site.usdz` are kept apart on purpose:** `Basemap.usdz` holds the ODbL features as a database, with their attributes, and `Site.usdz` is the Produced Work scene. `Basemap.usdz` composes onto `Site.usdz` as a sublayer.
- **`Site.dxf`** is the flat plan drawing of the site with its paper-space layouts, so a CAD user can plot the plan sheets.

## 7. Precision

**Precision is stated, and never finer than the source.** Elevation is exact and imagery untouched: both are lossless, and equal to the source pixel for pixel. Slope is stored to tenths of a degree, aspect to whole degrees and canopy height to whole metres. Every raster's data type, scale and no-data value are in the file table (§6) and again in its catalog entry (§9.5), so a reader decodes a pixel from the text and the manifest and never from a scale or offset tag a library may or may not honour.

⛔ **A reader that cannot decode a file's stated encoding skips the file and says so.** It never guesses at what a pixel means.

## 8. Levels

**Three levels: Max, Med and Min.** Max is the bundle in full. Med has half the resolution or count of Max and Min a quarter: in linear resolution for a grid, and in count for a mesh or a set of rows or features. A grid still lands on a size hosts accept. The meaning is the same on every site.

- **Every order builds all three.** A level is never priced and is the same for a free and a paid order.
- **A level belongs to the download, not to the file name.** A download holds exactly one level of each file, under the same name and path at every level. The manifest states the download's level once (§9.3), and the zip's name carries it.
- **Host-ready files carry the level; georeferenced files do not.** A `glb`, `usdz`, `obj`, `png` or `csv` is built at each level; `Terrain.mtl` is the same at every level. A `tif`, `gpkg`, `dxf`, `landxml` or `ifc` is byte-identical in all three downloads. By bytes a Med or Min download is therefore larger than a half or a quarter of Max; each download states its measured size.
- **A level omits or coarsens; it never moves, invents or reclassifies.** A coarser grid is resampled from the same source, a coarser mesh has fewer triangles over the same ground, and a coarser set keeps a subset of the same rows.
- **A request that names no level receives Max.** The level the platform recommends for a host is the platform's answer to that host, not a fact in the manifest.

## 9. The manifest

### 9.1 Where it is, and how it is found

`Manifest.json` sits at the root of the zip, beside `Readme.md`. It is the one file found by its name before anything else is read; every other file is found by its name in the manifest's catalog. The manifest states its version as a semver string; [`compatibility.md`](compatibility.md) says how a reader compares it.

### 9.2 Public facts only

The manifest is public, and holds only public facts: what the bundle is, where it is, what each file is and under what terms. It carries no commit, no image path, no build budget, threshold or parameter, and no register reserved for the platform's internal use. It has **one** file catalog, generated from the producer's registry and never written by hand.

### 9.3 The bundle statement

Facts about the whole bundle, stated once: the order's identity (the one identifier that joins a bundle to its vault entry), the AOI as a rectangle in longitude and latitude and as an area, the order's unit system and the delivery it resolved to, the time zone of the place, the build date, and **the level of this download**.

### 9.4 The two frames

Both frames are stated once, for every file of their family (§4).

- **The georeferenced frame:** the order's projected CRS, its vertical CRS and geoid, the horizontal linear unit and the vertical linear unit, each as a factor to metres so no reader maps "foot" to the wrong foot, and the order of the horizontal axes. It also states the grid rotation at the site origin: the angle between the CRS's grid north and true north there.
- **The local site frame:** the site origin, as longitude, latitude, height above the ellipsoid and height in the frame's vertical datum, and again as x, y and z in the projected CRS whose grid the frame's axes follow, with that CRS and its unit; the vertical datum its heights are in; and its unit, which is the metre on every order.

A CRS is named whole: a code, a PROJJSON object or a WKT2 string, with the horizontal and vertical CRS as separate references. No reader takes a compound code apart.

### 9.5 The catalog

One entry per file the download holds, keyed by its path in the zip. A file has the same name in storage as in the zip. Each entry states:

- **what it is:** its subject, its format and its family;
- **its bytes:** its size and its sha256;
- **its count:** rows for a table, features for a vector layer, triangles for a mesh, pixels for a raster. A count is what lets a reader estimate an import before starting it;
- **for a raster:** its width and height in pixels, its cell size, the rectangle it covers in its frame, its data type, scale, offset and no-data value (§7), its band names, and the legend of a class raster (each code and its class);
- **for a mesh:** its up axis, as its format fixes it (§4);
- **for a table:** its columns, and for the tree points the foliage-type vocabulary (§5);
- **its terms:** its ODbL role, its licence and its sources (§9.8).

### 9.6 What a bundle does not hold

The manifest lists every promised file that the bundle does not hold, each with its reason: the AOI has no such data, or the file is **pending** because a public-record source did not answer in time. A pending layer is added by the platform when the source answers, and the next download of the bundle includes it.

⛔ **A reader MUST show the stated reason rather than match on it.** The reasons can grow; a reader that branches on the ones it knows shows nothing for the one it does not. An absent file is not an error.

### 9.7 No per-host block

The manifest has **no** block for any one host. A reader finds each file it wants by its name in the catalog, and a name the catalog lacks is a file this download does not hold. Every value a 2.x plugin read from its host block has a neutral home in this page or a line saying it is dropped (§13). A new host needs nothing added to the manifest.

### 9.8 Integrity, licensing and attribution

⛔ **Every catalog entry carries a sha256, and a reader MUST verify it and MUST fail closed on a mismatch**, naming the file. Importing bytes that failed verification is worse than importing nothing. Write a download to a temporary path, verify it, and promote it by rename; a reader that streams onto the final path leaves a truncated file that looks cached.

Each entry states its **ODbL role** (an ODbL database, a Produced Work, or no ODbL source), the **licence** the file ships under as an SPDX expression, and its **sources**, each with its name and licence. ⛔ **A missing role, or one the reader does not know, reads as an ODbL database**, the stricter duty. The platform's [licensing](https://mantle.place/licensing) and [attributions](https://mantle.place/attributions) pages are the authority on the text those terms require.

### 9.9 Extras are not in the manifest

`Manifest.json` describes the bundle and names no Extra. The platform lists a bundle's Extras, in the vault and to a plugin that asks, and each Extra states its own sources and credits, since it travels without the bundle's Readme.

## 10. The zip

- **Members are compressed with DEFLATE.** A member that compression does not shrink MAY be stored instead. The length of a download is still known before its stream starts.
- **A download opens in the standard zip readers** (Windows Explorer, the .NET zip reader, the Python zip reader), and each member still verifies on its own against its catalog digest.
- **The zip's name carries the download's level.** Nothing else in the zip does, besides the manifest's one statement.
- **Archive paths are untrusted input.** A reader MUST resolve every path inside its extraction root and reject an entry that escapes it.
- **The media type** is `application/vnd.mantleplace.bundle+zip`, and the extension stays `.zip`, because `.mpb` belongs to other applications. A server that serves a bundle SHOULD send that type. A reader MUST NOT refuse a bundle because it arrived as `application/zip` or `application/octet-stream`: what makes a file a 3.x bundle is `Manifest.json` at its root.

## 11. Reading a bundle, start to finish

1. **Find the manifest.** `Manifest.json` at the root is a 3.x bundle. `Metadata/manifest.json` is a 2.x or 1.x bundle, read by [`format-2.md`](format-2.md). Neither is not a bundle.
2. **Check the version.** Parse the major; refuse a higher major gracefully, before extracting anything ([`compatibility.md`](compatibility.md) §3).
3. **Read the bundle statement and the two frames** (§9.3, §9.4), including the level.
4. **Find each file by its name in the catalog**, resolved inside the extraction root (§10). A name the catalog lacks is not held; the manifest says why (§9.6).
5. **Verify each file** against its sha256 (§9.8).
6. **Place each file by its family:** a georeferenced file by the order's CRS, a host-ready file by the local site frame, applying every published value verbatim (§4).
7. **Show what is missing and why,** pending layers included (§9.6).
8. **Honour each file's terms** (§9.8).

If a step is under-specified for a bundle you hold, that is a spec bug worth an issue, and a corpus bug too if no case catches it ([`conformance.md`](conformance.md)).

## 12. What left since 2.x, and how to derive it

Every 2.x file is either in the 3.x tree under a name, an Extra, or derived in one step from a file that stays. A curator who needs a removed file makes it from the 3.x file named here; a host plugin that needs one derives it the same way, from a pre-derived file, never by placing anything.

| 2.x file | 3.x file it comes from | The step |
| --- | --- | --- |
| `README.md`, `Metadata/manifest.json` | `Readme.md`, `Manifest.json` | Renamed and moved to the root |
| `Metadata/stac-item.json` (the STAC Item) | `Manifest.json` | Generate a STAC Item from the manifest's catalog |
| Every Shapefile set (`.shp` and its sidecars, the geometry splits such as `Water-Lines.shp` included) | The layer's `.gpkg` | Export the GeoPackage layer to Shapefile |
| The `-Projected` GeoPackage twins, and the geographic GeoPackages | The layer's `.gpkg`, already in the order's CRS | Reproject the GeoPackage, where another CRS is wanted |
| `Elevation/DEM.tif` | `Elevation/Elevation.tif` | Renamed |
| `Elevation/Heightmap.png` | `Elevation/Elevation.png` | Renamed |
| `Elevation/Surface.dxf`, `Surface.landxml` | `Elevation/Terrain.dxf`, `Terrain.landxml` | Renamed |
| `Elevation/SurfacePoints.csv` | `Elevation/Terrain.dxf` (or `Terrain.landxml`) | Export the surface's vertices as points |
| `Elevation/SteepSlopes.gpkg` | `Elevation/Slope.gpkg` | Renamed; `Slope.dxf` is its new pair |
| `Elevation/Slope.png`, `Aspect.png`, `Hillshade.png` | `Elevation/Slope.tif`, `Aspect.tif`, `Hillshade.tif` | Re-encode the raster as PNG, by the scale §6 states |
| `Elevation/Terrain.jpg` (the OBJ's texture) | `Imagery/Imagery.png`, named by `Terrain.mtl` | None: the material file points at the imagery |
| `Elevation/Terrain/` (the quantized-mesh tileset) | The terrain tiles Extra | Download the Extra beside the bundle |
| `Imagery/Imagery.pgw`, `Imagery.prj` | `Imagery/Imagery.tif` | Write a world file from the GeoTIFF's georeference |
| `Imagery/NDVI.tif`, `NDVI.png` | `Imagery/Imagery.tif` | `(band 4 - band 1) / (band 4 + band 1)` |
| `Imagery/Imagery-Engine.png` | `Imagery/Imagery.png`, now in the local site frame | None |
| `LandCover/LandCover.tif` | `Landcover/Landcover.tif` | Renamed |
| `LandCover/LandCover.png` | `Landcover/Landcover.tif` | Re-encode the raster as PNG |
| `LandCover/LandCoverAreas.gpkg`, `.dxf` | `Landcover/Landcover.gpkg`, `.dxf` | Renamed |
| `LandCover/CanopyHeight.tif` | `Landcover/Canopy.tif` | Renamed; stored to whole metres (§7) |
| `LandCover/CanopyHeight.png` | `Landcover/Canopy.tif` | Re-encode the raster as PNG |
| `LandCover/WaterMask.tif`, `WaterMask.png` | `Materials/Water.png` | Read a non-zero weight as water |
| `LandCover/MaterialWeights.tif`, `MaterialWeights-1To4.png`, `MaterialWeights-5To8.png` | The eight `Materials` PNGs | Pack four weight maps into the four channels of one texture |
| `LandCover/TreePoints.csv` | `Landcover/Trees.gpkg` (the order's frame) or `Landcover/Trees.csv` (the local site frame) | Renamed; the same six columns |
| `LandCover/TreePoints-Engine.csv` | `Landcover/Trees.csv` | None: it is now in the local site frame |
| `Features/StylizedMap.tif`, `.png` | `Basemap/Basemap.tif`, `.png` | Renamed |
| `Features/Buildings.glb` | `Basemap/Buildings.glb` | Moved |
| `Features/BuildingFootprints` | `Basemap/Buildings.gpkg`, `.dxf` | Renamed |
| `Features/RoadsDraped` | `Basemap/Roads.gpkg`, `.dxf` | Renamed |
| `Features/Roads` (the 2D centrelines) | `Basemap/Roads.gpkg` | Drop the heights |
| `Features/RoadsDraped-Engine.gpkg` | `Basemap/Roads.csv` | None: one row per vertex in the local site frame, with the road's name |
| `Features/RoadSurfaces` | `Basemap/Pavement.gpkg`, `.dxf` | Renamed |
| `Features/Water`, `Features/LandUse` | `Basemap/Water`, `Basemap/Landuse` | Renamed |
| `Records/FloodZones` | `Records/Floodplain` | Renamed; the DXF is new |
| `Records/HistoricPlaces` | `Records/Heritage` | Renamed |
| `Records/ProtectedAreas` | `Records/Conservation` | Renamed |
| `Records/CriticalHabitat` | `Records/Habitat` | Renamed |
| `Site/Model.ifc` | `Site/Site.ifc` | Renamed |
| `Site/Context.usdz` | `Site/Site.usdz` | Renamed |
| `Site/Features.usdz` | `Basemap/Basemap.usdz` | Renamed and moved |
| `Site/Brief.pdf` | The Site Brief Extra | Download the Extra beside the bundle |
| `Drawings/Linework.dxf` | `Site/Site.dxf` | Renamed and moved |
| `Drawings/SitePlan-Small.pdf`, `SitePlan-Large.pdf` | The plan sheets Extra; the same sheets are the layouts of `Site/Site.dxf` | Download the Extra, or plot the layouts |
| `Drawings/SitePlan-Small.svg`, `SitePlan-Large.svg` | The plan sheets Extra | Convert the PDF page to SVG |
| The `-Raw` level files | The same file at Max | None: the native file is in the bundle |
| The `-Med` and `-Min` level files (`Terrain-Med.glb`, `Context-Min.usdz` and the like) | The same name in the Med or Min download | Download that level |

The `Records` layers keep their names where 2.x already used one word (`Parcels`, `Zoning`, `Wetlands`, `Soils`, `Hydrography`, `Contamination`).

## 13. Where each 2.x host-block value lives in 3.x

The inventory in [`docs/import-inventory.md`](../docs/import-inventory.md#every-value-read-from-a-host-block) lists every value the Unreal, Revit and AutoCAD plugins read from a 2.x host block, and the values a host's download view depended on. Its row ids are used here. Each row has a neutral home in a 3.x manifest or a line saying it is dropped. "Catalog" is §9.5, "frames" §9.4, "bundle statement" §9.3 and "not held" §9.6.

| Row | 3.x home |
| --- | --- |
| R01 | Frames: the site origin's longitude and latitude |
| R02 | Frames: the site origin as x and y in the georeferenced frame's CRS, with its unit |
| R03 | Frames: the georeferenced frame's horizontal CRS |
| R04 | Frames: the georeferenced frame's vertical CRS, and the site origin's height in it |
| R05 | Frames: the grid rotation at the site origin |
| R06 | Dropped as a value: a file's family settles it. Every georeferenced file is absolute in the order's CRS, every host-ready file is offsets in the local site frame |
| R07 | Frames: the georeferenced frame's CRS, and the CRS the local site frame's axes follow |
| R08 | Frames: the site origin in the georeferenced frame, with its unit |
| R09 | Frames: the georeferenced frame's horizontal and vertical units; the local site frame is always metres |
| R10 | Not held: each promised file the bundle does not hold, with its reason. A per-host part has no verdict of its own: a host's part is the files it names |
| R11 | Catalog, by name: `Elevation/Terrain.dxf` (the surface) and `Site/Site.ifc`. The points file left; the plugin's own first route, the surface's triangles, reads `Terrain.dxf` (§12) |
| R12 | Catalog: every entry's sha256 |
| R13 | Frames, by the file's family |
| R14 | Dropped as a value: the family settles it (as R06) |
| R15 | Frames, by the family: a georeferenced file's heights are absolute in the order's vertical datum, and a host-ready file's are metres above the site origin's height |
| R16 | Frames: the georeferenced frame's units and vertical CRS, which `Elevation/Contours.dxf` is in |
| R17 | Catalog: `Imagery/Imagery.png` and the rectangle it covers in the local site frame, or `Imagery/Imagery.tif` and its georeference; the format is the extension |
| R18 | Bundle statement: the download's level. Catalog: each file's count, which states what the level costs. The per-file choice of a level, its cuts and its pointers are dropped: a download holds one level of every file (§8), and the counts of all three levels are the platform's answer to a host |
| R19 | Catalog, by name: each layer's `.gpkg` (`Basemap/Roads`, `Landuse`, `Water`, `Pavement`, `Landcover/Landcover`, `Records/Floodplain`, `Elevation/Slope`), every one in the order's CRS |
| R20 | Catalog: one GeoPackage per layer; the entry names its tables |
| A01 | Dropped as a value: the family settles it (as R06) |
| A02 | Frames: the georeferenced frame's CRS |
| A03 | Frames: the site origin in the georeferenced frame, with its unit |
| A04 | Frames: the georeferenced frame's horizontal and vertical units |
| A05 | Catalog, by name: `Elevation/Contours.dxf`, `Elevation/Terrain.landxml`, `Imagery/Imagery.png`, `Site/Site.dxf` and each `Records/*.dxf` |
| A06 | The file's name: `Records/Zoning.dxf` is the Zoning layer |
| A07 | Catalog: every entry's sha256 |
| A08 | Catalog: the format; the extension says it too |
| A09 | Dropped as a value: the family settles it (as R06) |
| A10 | Frames, by the file's family |
| A11 | Frames, by the family (as R15) |
| A12 | Frames: the georeferenced frame's vertical CRS |
| A13 | Catalog: the width and height of `Imagery/Imagery.png` |
| A14 | Catalog: the rectangle `Imagery/Imagery.tif` covers in the order's CRS and unit, which is the insertion point and the size; `Imagery.png` at Max holds the same pixels. The rotation is zero: every georeferenced raster is north-up on the order's grid |
| A15 | Not held, as R10. "Requires" (a surface needs Civil 3D) is dropped: it is a fact about a host, not about a bundle |
| U01 | Catalog: `Elevation/Elevation.png` and its sha256 |
| U02 | Catalog: the width and height of `Elevation/Elevation.png` |
| U03 | Fixed by this text: row 0 of every raster is its north edge (§4) |
| U04 | Catalog: the offset and step of `Elevation/Elevation.png` (§6) |
| U05 | Catalog: the cell size of `Elevation/Elevation.png` in metres, and its offset and step, which the host multiplies into its own units. The section size and component counts are dropped: they are the engine's layout for the stated pixel size, and the producer writes `Elevation.png` at a size that layout accepts |
| U06 | Catalog: `Imagery/Imagery.png` and its sha256, in the local site frame on every order |
| U07 | Catalog: the rectangle `Imagery/Imagery.png` covers in the local site frame |
| U08 | Catalog: the rectangles of `Elevation/Elevation.png` and `Imagery/Imagery.png`, both in the local site frame; a reader compares the two as stated. The separate claim is dropped |
| U09 | Catalog: `Elevation/Terrain.glb`, its sha256 and its up axis, which glTF fixes (§4) |
| U10 | Dropped: a host's own choice. The catalog's triangle count is the fact it can choose by |
| U11 | Not held, as R10 |
| U12 | Catalog: `Basemap/Buildings.glb` and its up axis |
| U13 | Catalog, by name: the eight `Materials` PNGs, whose names are the paint layers' names. The engine copies of NDVI, slope, aspect, land cover, canopy and hillshade left; each derives from its GeoTIFF (§12) |
| U14 | Catalog: each PNG's data type, width, height, size and sha256 |
| U15 | Catalog: each raster's scale, offset, no-data value, band names and class legend (§7) |
| U16 | Catalog: `Landcover/Trees.csv`, its sha256 and its row count |
| U17 | Frames: `Trees.csv` is host-ready, so it is in the local site frame in metres |
| U18 | Bundle statement: the download's level; the Med and Min `Trees.csv` are the first rows of Max (§6) |
| U19 | Catalog: `Basemap/Roads.csv` in the local site frame, with each road's name; its heights are in the local site frame's vertical datum. `Basemap/Roads.gpkg` is the same roads in the order's CRS |
| U20 | Frames: the CRS the local site frame's axes follow |
| U21 | Frames: the site origin as x and y in that CRS |
| U22 | Frames: the site origin's height in the vertical datum |
| U23 | Dropped: the local site frame is flat by definition (§4) |
| T01 | Dropped from the manifest: the terrain tiles are an Extra, which the platform lists and a signed-in plugin downloads (§9.9) |
| T02 | Dropped from the manifest: the platform's listing of the Extra states it |
| T03 | Bundle statement: the AOI rectangle in longitude and latitude |
| T04 | Catalog: as U06 and U07 |
| T05 | Frames: the site origin's longitude, latitude and height |
| O01 | Catalog: `Landcover/Trees.csv` and `Landcover/Trees.gpkg`. No view question remains: a download holds every file of the tree at one level |
| O02 | Not applicable, as in 2.x: a download holds every file the AutoCAD import reads |
| O03 | Catalog: `Records/Floodplain.gpkg` |
| O04 | Dropped from the manifest: the terrain tiles Extra, as T01 |

**The values a 2.x block declared and no plugin read** (the inventory's last list: the version floors, the unit notes, the formats, the ground sample distances, the caps, the ranks, the notes and the rest) are dropped. The facts among them that a 3.x reader can use are in the catalog already: a file's format, its size, its count and its pixel size.

## 14. Stability, and a higher major

**Within 3.x nothing breaks.** The full promise is [`compatibility.md`](compatibility.md) §8: the tree, the names, the families, the formats and the precision of every file only grow, and no file is removed, moved or renamed.

**A higher major is refused gracefully.** A reader opens `Manifest.json` at the root, reads its version and nothing else, and compares the parsed major. If it is higher than any the reader supports, the reader stops before it extracts a file, and tells the user the bundle's version, the versions it supports, and that the software needs updating.

## 15. The file-by-host table

Before this text was frozen, three bundles were repacked into this tree and every file was opened in eight hosts with their own importers. The result, a row per file and a column per host, is [`file-by-host.md`](file-by-host.md). It is evidence for the tree, not part of the contract: a host's importer can change, and the promise in §3 does not.
