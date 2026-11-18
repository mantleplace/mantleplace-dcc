# Revit 1.2.0: release notes

The body the `revit-1.2.0` release takes, titled `revit-1.2.0 — Mantle Place for Revit` and written to
[`docs/releases.md`](../../docs/releases.md), then the detail behind its MPB 3.0 lines. A 2.x bundle imports exactly
as in 1.1.1. The **Verification** section holds the release gate's results on the packaged zip; its published-asset
and storefront lines are filled in when the release is cut and the zip is submitted. The published body cites each pull request by its bare number, as
[`docs/releases.md`](../../docs/releases.md) asks; this file links each number instead, because a bare number in a
tracked file is refused by the public-reference gate.

## The release body

```markdown
**Revit 2025.5 or later, 2026.5 or later, and 2027, Windows 64-bit.** One build serves all three. Close Revit, extract the zip and run `MantlePlace.bundle\Install.cmd`, or copy the files directly inside `MantlePlace.bundle\Contents` into your add-ins folder as `README.txt` describes. The plugin is unsigned, so each Revit version asks once: choose **Always Load**. [Install guide](https://github.com/mantleplace/mantleplace-dcc/blob/main/revit/README.md) · [mantle.place/revit](https://mantle.place/revit)
Upgrade by closing Revit and running the new `Install.cmd` over the old one; see below for what an MPB 3.0 order changes.

## Upgrading from 1.1.1

- **An MPB 3.0 order is downloaded at one level.** The vault shows `Fidelity Level` with Max, Med and Min, each with its download size and the platform's recommendation preselected. A 2.x order downloads as before.
- **Some 2.x items are not part of an MPB 3.0 import.** The toposurface points file, cropping the terrain and contours to the area of interest, the project's time zone, and the flood map's and steep ground's details on the hazard plan's key. Each is shown as skipped, with its reason, in the import window and the import log.
- **An earlier version cannot download an MPB 3.0 order.** The platform asks it to update to this one first.

## What's new

- **MPB 3.0 bundles import**, beside 2.x: terrain, contours, context buildings and the linked site model, shared coordinates, roads, road surfaces, water, land use, land cover, trees, the imagery drape and the hazard plan, from the vault, from a zip on disk, and again over an earlier import ([654](https://github.com/mantleplace/mantleplace-dcc/pull/654), [662](https://github.com/mantleplace/mantleplace-dcc/pull/662), [667](https://github.com/mantleplace/mantleplace-dcc/pull/667), [676](https://github.com/mantleplace/mantleplace-dcc/pull/676)).
- **Choose the level before the download.** Max, Med or Min, each with its size; a send from the web vault opens on the recommendation, and the level you choose stays chosen while the vault window refreshes ([660](https://github.com/mantleplace/mantleplace-dcc/pull/660), [694](https://github.com/mantleplace/mantleplace-dcc/pull/694)).
- **A 3.0 layer's import time is estimated before you start**, and a layer estimated at over five minutes starts unticked, saying why ([684](https://github.com/mantleplace/mantleplace-dcc/pull/684)).
- **About is a window** in place of a dialog, with a link to the privacy page; its Logs button opens the same folder as the ribbon's Open Logs ([646](https://github.com/mantleplace/mantleplace-dcc/pull/646)).
- **Fixes for 3.0 bundles:** trees and shrubs stand on the toposolid at the site, not far from it ([714](https://github.com/mantleplace/mantleplace-dcc/pull/714)); shrubs are planted as shrubs ([702](https://github.com/mantleplace/mantleplace-dcc/pull/702)); two 3.0 orders in one project each get their own land and hazard plans ([700](https://github.com/mantleplace/mantleplace-dcc/pull/700)); and a 3.0 import's heights are checked against the vertical datum a 2.x import recorded, by name ([698](https://github.com/mantleplace/mantleplace-dcc/pull/698)).

## Verification

- Built from `<short sha>` on `main`. <Gate result at that commit, on the packaged zip installed clean in Revit 2027 and signed in on the dev platform: the ribbon commands, one dev-built 3.0 order imported at Max, Med and Min, one 2.x order imported, and a bundle sent from the web vault and imported.>

  - `revit.plugin-loads <result line>`
  - `revit.import-check <result line>, bundle 3.0.0 level Max`
  - `revit.import-check <result line>, bundle 3.0.0 level Med`
  - `revit.import-check <result line>, bundle 3.0.0 level Min`
  - `revit.import-check <result line>, bundle 2.x`
  - `revit.connected-host <result line>`
  - `vault.round-trip <result line>`

- Published asset: <check from step 4 and its date>.
- `MantlePlace-Revit-1.2.0.zip` sha256 `<sha256 of the zip that passed the gate>`
- Storefront: <submitted to the Autodesk storefront on date>, from the asset above.

Previous: [revit-1.1.1](https://github.com/mantleplace/mantleplace-dcc/releases/tag/revit-1.1.1)
```

## The two store review fixes

The storefront review of `revit-1.1.0` found two faults, both fixed in
[629](https://github.com/mantleplace/mantleplace-dcc/pull/629) and first published in `revit-1.1.1`. 1.2.0 is the
first version submitted to the storefront that carries them:

- **Open in Curator's size dialog shows.** It stopped with "Corresponding button not found" in any project with a
  site location and no import.
- **No plugin window shows the Revit logo.** Sign In, Vault and the import window carry the Mantle Place mark in
  their title bars.

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
| Trees and shrubs | `Landcover/Trees.csv`, offsets from the site origin with heights above the origin's height, each point's foliage type read from the vocabulary its catalog entry states |
| Satellite imagery drape | `Imagery/Imagery.png`, on the rectangle its catalog entry states |
| Flood zones on the hazard plan | `Records/Floodplain.gpkg` |
| Steep ground on the hazard plan | `Elevation/Slope.gpkg` |

A step whose file the bundle does not hold is shown as skipped, naming the file, with the manifest's own
reason where it gives one.

A 3.0 bundle imports from the vault, from a zip on disk, and again over an earlier import, by the same rules
as a 2.x bundle. The project's shared coordinates are turned by the grid rotation the manifest's local frame
states, verbatim. A layer file whose catalog entry names its tables is read
at the table of the step's geometry (the water bodies from `Water_polygon`), and the manifest's credits are
the import's attribution. The land and hazard plans are named for the order, since a 3.0 manifest states no
job id.

## Choosing a level before the download

An MPB 3.0 order from the vault is downloaded at one level. The vault shows `Fidelity Level` with Max, Med
and Min, each with its download size and the platform's recommendation preselected, and Import downloads the
chosen level. The download holds only the files listed above, at that level, with the manifest. The plugin
keeps one download per order: another level is a fresh download that replaces it. A bundle sent from the web
vault opens its import window on the recommendation, with the same control. A 2.x order downloads as in
1.1.1, with no level to choose.

A 3.0 layer has no per-level list, so its estimate in the import window is its file's count in the catalog
times the measured cost of one unit. A layer whose estimate is over five minutes starts unticked, with the
estimate and one line saying why; ticking it imports all of it. 2.x estimates are unchanged.

## Every 2.x item an MPB 3.0 import leaves out

What a 2.x bundle brought into the project that a 3.0 bundle does not, and why. Every other 2.x step has its 3.0
file in the table above. Each is shown as skipped, with its reason, in the import window and the import log.

| Item | Why |
| --- | --- |
| The toposurface points file | MPB 3.0 ships no points file, and the plugin never converts a georeferenced file; the terrain is built from Elevation/Terrain.dxf. |
| Cropping the terrain and contours to the area of interest | MPB 3.0 states the area of interest only in longitude and latitude, and no reader projects; the terrain is built from the whole surface. |
| The project's time zone | MPB 3.0 names the time zone without the UTC offset Revit needs, and the plugin does not work one out; the site location is still set. |
| The flood map's zones, panels and effective dates on the hazard plan's key | MPB 3.0 states none of them; the flood zones are still drawn from Records/Floodplain.gpkg. |
| The steep-ground threshold on the hazard plan's key | MPB 3.0 publishes no build threshold; the steep ground is still drawn from Elevation/Slope.gpkg. |
