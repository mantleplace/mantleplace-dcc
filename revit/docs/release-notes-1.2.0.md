# Revit 1.2.0: what an MPB 3.0 bundle imports

The notes the `revit-1.2.0` release body takes for importing an MPB 3.0 bundle ([`docs/releases.md`](../../docs/releases.md)
holds the body's template). A 2.x bundle imports exactly as in 1.1.

## What an MPB 3.0 bundle imports

Every step finds its file by its name in the manifest's catalog ([`spec/format.md`](../../spec/format.md) §6, §9.5),
in the frame that file's family states (§4): a georeferenced file is placed against the site origin in the
order's CRS and units, and a host-ready file is offsets from the site origin in metres. Nothing is converted,
and a 3.0 download holds one level of every file, so no step takes a per-category level.

| Step | The 3.0 file it reads |
| --- | --- |
| Terrain (toposolid from the surface's triangles, or the DXF linked when they cannot be placed) | `Elevation/Terrain.dxf` |
| Published contours | `Elevation/Contours.dxf` |
| Context buildings, and the linked site model | `Site/Site.ifc` |
| Shared coordinates and the site location | The manifest's site origin, verbatim |
| Road centrelines | `Basemap/Roads.gpkg` |
| Road surfaces | `Basemap/Pavement.gpkg` |
| Water | `Basemap/Water.gpkg` |
| Land use plan | `Basemap/Landuse.gpkg` |
| Land cover plan | `Landcover/Landcover.gpkg` |
| Trees | `Landcover/Trees.csv` |
| Satellite imagery drape | `Imagery/Imagery.png`, on the rectangle its catalog entry states |
| Flood zones on the hazard plan | `Records/Floodplain.gpkg` |
| Steep ground on the hazard plan | `Elevation/Slope.gpkg` |

A step whose file the bundle does not hold is shown as skipped, naming the file, with the manifest's own
reason where it gives one.

A 3.0 bundle imports from the vault, from a zip on disk, and again over an earlier import, by the same rules
as a 2.x bundle; a 3.0 import over a 2.x import of the same order is the same site, not a new one. The
project's shared coordinates are not turned by the grid rotation the manifest states: every file is drawn
on the order's grid already, as a 2.x bundle's was. A layer file whose catalog entry names its tables is read
at the table of the step's geometry (the water bodies from `Water_polygon`), and the manifest's credits are
the import's attribution.

## Left out of an MPB 3.0 import

Each is shown as skipped, with its reason, in the import window and the import log.

| Item | Why |
| --- | --- |
| The toposurface points file | MPB 3.0 ships no points file, and the plugin never converts a georeferenced file; the terrain is built from Elevation/Terrain.dxf. |
| Cropping the terrain and contours to the area of interest | MPB 3.0 states the area of interest only in longitude and latitude, and no reader projects; the terrain is built from the whole surface. |
| The project's time zone | MPB 3.0 names the time zone without the UTC offset Revit needs, and the plugin does not work one out; the site location is still set. |
| The flood map's zones, panels and effective dates on the hazard plan's key | MPB 3.0 states none of them; the flood zones are still drawn from Records/Floodplain.gpkg. |
| The steep-ground threshold on the hazard plan's key | MPB 3.0 publishes no build threshold; the steep ground is still drawn from Elevation/Slope.gpkg. |
