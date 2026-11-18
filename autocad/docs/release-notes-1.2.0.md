# AutoCAD 1.2.0: what an MPB 3.0 bundle imports

The notes the `autocad-1.2.0` release body takes for importing an MPB 3.0 bundle ([`docs/releases.md`](../../docs/releases.md)
holds the body's template). A 2.x bundle imports exactly as in 1.1: the same steps, placed the same way, and a
2.x send from the web vault begins as its window opens, as before. What is new for a 2.x bundle is one more
way in: Import in the vault browser, which 1.1 did not offer in AutoCAD, runs the same import.

## What an MPB 3.0 bundle imports

Every step finds its file by its name in the manifest's catalog ([`spec/format.md`](../../spec/format.md) §6, §9.5),
in the frame that file's family states (§4). Each file AutoCAD reads is a georeferenced one: it is placed
in the order's CRS and in the horizontal and vertical units that frame states, the units the drawing is
checked against. Nothing is converted or reprojected, and no transform is worked out.

| Step | The 3.0 file it reads |
| --- | --- |
| Contours | `Elevation/Contours.dxf` |
| Aerial imagery, as a raster image | `Imagery/Imagery.tif`, inserted at the rectangle its catalog entry states in the georeferenced frame, aligned with the axes of the order's CRS (rotation 0 in that system); the plugin derives no rotation |
| Public-record layers, one layer a file, named by the file | each `Records/*.dxf` the catalog names |
| Site linework | `Site/Site.dxf` |

A step whose file the bundle does not hold is shown as skipped, naming the file, with the manifest's own
reason where it gives one. A record layer the bundle does not hold is named, with its reason, after the
layers that were placed.

A 3.0 bundle imports from the vault browser, from an export sent from the web vault, and from a zip on disk
(Import Bundle), by the same rules as a 2.x bundle. The drawing's geographic location, and in Civil 3D its
coordinate system, are checked against the order's CRS as before. The import window waits for Import only
when it offers a choice: a send from the web vault whose order offers levels opens saying what the import
will do, with the level control, and another level chosen there is downloaded and the window reopened on
it before anything is drawn. Every other import, with nothing to choose, begins as the window opens.

The aerial is placed from the georeferenced `Imagery/Imagery.tif` alone; the host-ready
`Imagery/Imagery.png` is not read, and is not a step of the import.

## Left out of an MPB 3.0 import

Each is shown as skipped, with its reason, in the import window and on the command line.

| Item | Why |
| --- | --- |
| The Civil 3D surface | This version builds no surface from an MPB 3.0 bundle, so `Elevation/Terrain.landxml` is left in the bundle; the contours are still drawn from Elevation/Contours.dxf. |
| An aerial whose catalog entry states no rectangle, states no frame for it, or states a frame other than the georeferenced one | The plugin never works out an insertion, never assumes a frame and never reprojects; the aerial is skipped, saying what the catalog states or does not. |
| The readiness verdicts ("requires") | MPB 3.0 publishes none (A15): a file the catalog names is ready, and one it does not is not held. |
