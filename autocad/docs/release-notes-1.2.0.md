# AutoCAD 1.2.0: release notes

The body the `autocad-1.2.0` release takes, titled `autocad-1.2.0 — Mantle Place for AutoCAD` and written to
[`docs/releases.md`](../../docs/releases.md), then the detail behind its MPB 3.0 lines. The version goes from 1.0.0
to 1.2.0, with no 1.1.0, so that both Autodesk plugins carry one number for the MPB 3.0 release. A 2.x bundle
imports exactly as in 1.0: the same steps, placed the same way, and a 2.x send from the web vault begins as its
window opens, as before. What is new for a 2.x bundle is more ways in: Import Bundle and Import in the vault browser,
which 1.0 did not offer, run the same import. The **Verification** section holds the release gate's results on the
packaged zip; its published-asset and storefront lines are filled in when the release is cut and the zip is
submitted. The published body cites each pull request by its bare number, as
[`docs/releases.md`](../../docs/releases.md) asks; this file links each number instead, because a bare number in a
tracked file is refused by the public-reference gate.

## The release body

```markdown
**AutoCAD and Civil 3D 2025, 2026 and 2027, Windows 64-bit.** One package serves both products and all three years: close AutoCAD, extract the zip and run `MantlePlaceAutoCad.bundle\Install.cmd`, or copy the `MantlePlaceAutoCad.bundle` folder into `%APPDATA%\Autodesk\ApplicationPlugins` as `README.txt` describes. The plugin is unsigned, so each AutoCAD version asks once: choose **Always Load**. [Install guide](https://github.com/mantleplace/mantleplace-dcc/blob/main/autocad/README.md)
Upgrade by closing AutoCAD and running the new `Install.cmd` over the old one; see below for what an MPB 3.0 order changes.

## Upgrading from 1.0.0

- **An MPB 3.0 order is downloaded at one level.** The vault shows Max, Med and Min, each with its download size and the platform's recommendation preselected. A 2.x order downloads as before.
- **Some items are not part of an MPB 3.0 import.** A Civil 3D surface whose LandXML states another unit or coordinate system than the order's, an aerial whose catalog entry states no rectangle in the order's frame, and a public-record layer that holds anything but polylines. Each is shown as skipped, with its reason, in the import window and on the command line.
- **In Civil 3D, set the drawing's units to the order's before you import.** The surface is now skipped, naming both units, in a drawing whose Civil 3D drawing units (Drawing Settings, Units and Zone) are not the order's, from any bundle. 1.0.0 built it there, scaled away from every other part.
- **An earlier version cannot import an MPB 3.0 order.** The platform asks it to update to this one first.

## What's new

- **MPB 3.0 bundles import**, beside 2.x: contours, the Civil 3D surface, the aerial, public-record layers and site linework, each from the file the bundle's catalog names, in the order's own coordinates and units; from the vault, from a zip on disk and from a send out of the web vault ([654](https://github.com/mantleplace/mantleplace-dcc/pull/654), [662](https://github.com/mantleplace/mantleplace-dcc/pull/662), [716](https://github.com/mantleplace/mantleplace-dcc/pull/716), [718](https://github.com/mantleplace/mantleplace-dcc/pull/718)).
- **Choose the level before the download.** Max, Med or Min, each with its size; a send from the web vault opens on the recommendation, and the level you choose stays chosen while the vault window refreshes ([660](https://github.com/mantleplace/mantleplace-dcc/pull/660), [694](https://github.com/mantleplace/mantleplace-dcc/pull/694)).
- **Import Bundle** imports a bundle zip from disk, with no account needed ([636](https://github.com/mantleplace/mantleplace-dcc/pull/636)).
- **Every import runs in one import window**, step by step, with Cancel heard between steps; a cached download is reused only for its host and level ([632](https://github.com/mantleplace/mantleplace-dcc/pull/632), [648](https://github.com/mantleplace/mantleplace-dcc/pull/648), [649](https://github.com/mantleplace/mantleplace-dcc/pull/649), [705](https://github.com/mantleplace/mantleplace-dcc/pull/705)).
- **Sign-in, the vault, About and notices are new windows**, shared with the Revit plugin; the vault offers Import, and About links the privacy page ([646](https://github.com/mantleplace/mantleplace-dcc/pull/646)).

## Known issues

- **AutoCAD asks before loading the plugin**, because it is not signed: choose **Always Load**.

## Verification

- Built from `<short sha>` on `main`. <Release gate at that commit, against this zip installed clean in AutoCAD 2027.>
- Published asset: <check from step 4 and its date>.
- `MantlePlace-AutoCad-1.2.0.zip` sha256 `<hash>`
- Storefront: <submitted to the Autodesk storefront on date>, from the asset above.

Previous: [autocad-1.0.0](https://github.com/mantleplace/mantleplace-dcc/releases/tag/autocad-1.0.0)
```

## What an MPB 3.0 bundle imports

Every step finds its file by its name in the manifest's catalog ([`spec/format.md`](../../spec/format.md) §6, §9.5),
in the frame that file's family states (§4). Each file AutoCAD reads is a georeferenced one: it is placed
in the order's CRS and in the horizontal and vertical units that frame states, the units the drawing is
checked against. Nothing is converted or reprojected, and no transform is worked out.

| Step | The 3.0 file it reads |
| --- | --- |
| Contours | `Elevation/Contours.dxf` |
| Civil 3D surface, a TIN surface (Civil 3D only) | `Elevation/Terrain.landxml`, absolute in the order's CRS, its coordinates and heights in the one linear unit the file states, which must be both the georeferenced frame's horizontal and its vertical unit |
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

The Civil 3D surface is built by Civil 3D from `Elevation/Terrain.landxml`, which places the file's points as
written, as from a 2.x bundle. Before Civil 3D reads the file, the plugin checks what the file says of itself
against the frame the manifest states for it. A LandXML file states one linear unit, which governs its X, Y and
its heights alike, so the linear unit of its `<Units>` element must be both the georeferenced frame's horizontal
unit and its vertical unit; where the order's two units differ (coordinates in US survey feet and heights in
feet, say), one LandXML unit cannot state both, and no surface is built. The EPSG code of its `<CoordinateSystem>`
element must be the order's CRS. Neither is assumed (`HPS-53`): a file that states no linear unit, or no EPSG code
(no `<CoordinateSystem>`, or one that names its system only by name), builds no surface, and the step says which
it does not state. A file that states another unit or coordinate system, or a value the plugin does not know,
builds no surface, and the step says what the file states and what the order states; the plugin never scales or
reprojects it. In plain AutoCAD, and in Civil 3D started as AutoCAD, the step says the surface needs Civil 3D.
A bundle whose catalog does not hold the surface file says the surface is not in this bundle, with the
manifest's own reason where it gives one, and imports the rest.

The aerial is placed from the georeferenced `Imagery/Imagery.tif` alone; the host-ready
`Imagery/Imagery.png` is not read, and is not a step of the import.

## Left out of an MPB 3.0 import

Each is shown as skipped, with its reason, in the import window and on the command line.

| Item | Why |
| --- | --- |
| A Civil 3D surface whose `Elevation/Terrain.landxml` states a unit or coordinate system other than the order's, states none, or whose order's coordinates and heights are in two different units | The plugin never converts or reprojects a file; the surface is skipped, naming what the file states and what the order states, and the contours are still drawn from `Elevation/Contours.dxf`. |
| A Civil 3D surface, from any bundle, in a drawing whose Civil 3D drawing units are not the order's (a drawing in feet for an order in metres, or one in metres for an order in feet) | Civil 3D converts a LandXML file to the drawing's units as it reads it, which would build the surface away from every other part. The plugin never changes a drawing's units; the surface is skipped, naming both units and what to set (Drawing Settings, Units and Zone), and the rest is placed. Earlier versions built the surface there, scaled. |
| An aerial whose catalog entry states no rectangle, states no frame for it, or states a frame other than the georeferenced one | The plugin never works out an insertion, never assumes a frame and never reprojects; the aerial is skipped, saying what the catalog states or does not. |
| A public-record layer whose DXF carries an entity other than a polyline (a point or a hatch, as `Records/Contamination.dxf`, `Records/Heritage.dxf` and `Records/Floodplain.dxf` may) | As in 1.0, only polylines are placed, and a layer is never placed in part: the layer is named as not placed, with the entity it carries, and the other layers are placed. |
| The readiness verdicts ("requires") | MPB 3.0 publishes none (A15): a file the catalog names is ready, and one it does not is not held. |
