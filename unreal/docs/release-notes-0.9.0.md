# Unreal 0.9.0: what an MPB 3.0 bundle imports

The notes the `unreal-0.9.0` release body takes for importing an MPB 3.0 bundle ([`docs/releases.md`](../../docs/releases.md)
holds the body's template). A 2.x bundle imports exactly as in 0.8.

## What an MPB 3.0 bundle imports

Every step finds its file by its name in the manifest's catalog ([`spec/format.md`](../../spec/format.md) §6, §9.5),
in the local site frame, in each import mode (Landscape, Mesh, Both), from the vault and from a zip on disk,
and again on a re-import.

| Step | The 3.0 file it reads |
| --- | --- |
| Landscape | `Elevation/Elevation.png` |
| Landscape paint layers | The eight `Materials/*.png` weight maps, each layer named by its file |
| Imagery drape | `Imagery/Imagery.png` |
| Terrain mesh | `Elevation/Terrain.glb` |
| Buildings | `Basemap/Buildings.glb` |
| Tree points | `Landcover/Trees.csv` |
| Road splines | `Basemap/Roads.csv` |
| Public-record layers | Each `Records/<Name>.dxf`, drawn as one toggleable overlay of lines laid on the Landscape; each `Records/<Name>.gpkg` is verified and kept in the bundle |

A record layer is placed only where the order's georeferenced frame, which the manifest states once, is the frame this host places in (metric, the grid the site origin is stated in); a file in any other grid is named and left out, never converted. Each layer shows the sources and licence its catalog entry states as its credit, a standing notice, and every record file the download does not hold with the reason the manifest gives. The 3.0 manifest carries no per-layer disclaimer or notice text, so none is shown beside a layer; a per-layer disclaimer and notice in the manifest is an open data-contract request (the issue titled "Data contract: per-layer disclaimer and notice for public-record layers"), and the plugin shows what the manifest states once it does.

## Choosing a level before the download

An MPB 3.0 order from the vault is downloaded at one level. The import window shows `Fidelity Level`
with Max, Med and Min, each with its download size and the platform's recommendation preselected and
marked `Recommended`, and `Download` fetches the chosen level. The download holds only the files listed
above, at that level, with the manifest. The plugin keeps one download per order: importing the same
level again reuses it with no download, and choosing another level downloads that level and replaces
it. An import with no window to choose in (a script) takes the recommendation. A 2.x order downloads as
in 0.8, with no level to choose.

## Layers left out of an MPB 3.0 import

Each is shown as skipped, with its reason, in the import window and the import log.

| Layer | Why |
| --- | --- |
| Coverage rasters (slope, aspect, hillshade, land cover, canopy height, NDVI) | MPB 3.0 ships them only as georeferenced GeoTIFFs, and the plugin never converts a georeferenced file. |
| Water mask | MPB 3.0 ships no separate mask: the water coverage is `Materials/Water.png`, painted as the `Water` layer. |

## Streaming into Cesium

An MPB 3.0 bundle holds no terrain tiles: the platform lists them as an Extra beside the bundle. The first
stream of a downloaded 3.0 order fetches them (a signed-in session is needed), keeps them beside the bundle's
zip and streams when they arrive; a second stream reuses them. An order the platform lists no terrain tiles
for, or a zip from outside the plugin's download folder with none beside it, is refused with the reason. A 2.x
bundle streams its own tiles, as in 0.8.
