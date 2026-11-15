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
| Public-record layers | Not imported from an MPB 3.0 bundle in this release. |
| Streaming into Cesium | The terrain tiles are an Extra beside an MPB 3.0 bundle, not part of it; not offered in this release. |
