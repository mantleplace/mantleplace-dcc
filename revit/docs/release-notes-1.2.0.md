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
- **Import an MPB 3.0 order into a project that holds no earlier import.** A project that already holds one refuses an MPB 3.0 bundle, the same order again included, says why, and is left unchanged. This holds until the format settles which angle to true north a 3.x host applies ([677](https://github.com/mantleplace/mantleplace-dcc/issues/677)).

## What's new

- **MPB 3.0 bundles import**, beside 2.x: terrain, contours, context buildings and the linked site model, shared coordinates, roads, road surfaces, water, land use, land cover, trees, the imagery drape and the hazard plan, from the vault, from a zip on disk and from a send out of the web vault ([654](https://github.com/mantleplace/mantleplace-dcc/pull/654), [662](https://github.com/mantleplace/mantleplace-dcc/pull/662), [667](https://github.com/mantleplace/mantleplace-dcc/pull/667), [676](https://github.com/mantleplace/mantleplace-dcc/pull/676)).
- **Choose the level before the download.** Max, Med or Min, each with its size; a send from the web vault opens on the recommendation, and the level you choose stays chosen while the vault window refreshes ([660](https://github.com/mantleplace/mantleplace-dcc/pull/660), [694](https://github.com/mantleplace/mantleplace-dcc/pull/694)).
- **A 3.0 layer's import time is estimated before you start**, and a layer estimated at over five minutes starts unticked, saying why ([684](https://github.com/mantleplace/mantleplace-dcc/pull/684)).
- **About is a window** in place of a dialog, with a link to the privacy page; its Logs button opens the same folder as the ribbon's Open Logs ([646](https://github.com/mantleplace/mantleplace-dcc/pull/646)).
- **Fixes for 3.0 bundles:** trees and shrubs stand on the toposolid at the site, not far from it ([714](https://github.com/mantleplace/mantleplace-dcc/pull/714)); shrubs are planted as shrubs ([702](https://github.com/mantleplace/mantleplace-dcc/pull/702)); a 3.0 order that lacks a layer the site does not have still downloads, and its step is shown as skipped ([715](https://github.com/mantleplace/mantleplace-dcc/pull/715)); and a 3.0 order sent from the web vault is taken, where it was refused as a version the plugin does not read ([715](https://github.com/mantleplace/mantleplace-dcc/pull/715)).

## Verification

- Built from `156146c` on `main`. Release gate (2026-11-18) at that commit, against this zip installed clean by the host harness in Revit 2027 and signed in on the dev platform: the plugin loaded with its ribbon; its sign-in, vault, About and notice windows opened and read as the plugin's labels say; one platform-built MPB 3.0 order was downloaded and imported at Max, at Med and at Min, every layer the import window offers, unattended; a 2.x bundle imported; and the same 3.0 order sent from the web vault was received and imported.

  - `revit.plugin-loads`: pass, 8 of 8
  - `revit.plugin-windows`: pass, 50 of 50
  - `revit.import-check`, bundle 3.0.0 level Max: pass, 41 of 41
  - `revit.import-check`, bundle 3.0.0 level Med: pass, 41 of 41
  - `revit.import-check`, bundle 3.0.0 level Min: pass, 41 of 41
  - `revit.import-check`, bundle 2.2.0: pass, 18 of 18
  - `revit.connected-host`: 25 of 27. Its presence and import steps passed. Its cancel step sends the same 3.0 order to the same project a second time, and the plugin refused it before its import window opened, as Upgrading says it does ([677](https://github.com/mantleplace/mantleplace-dcc/issues/677)); cancelling a sent bundle was not exercised.
  - `vault.round-trip`: pass, 27 of 27

- The same zip, unchanged, on Revit 2025 and on Revit 2026 (2026-11-18), installed clean by the host harness in each and signed in on the dev platform: the plugin loaded with its ribbon; its sign-in, vault, About and notice windows opened and read as the plugin's labels say; the same platform-built MPB 3.0 order was downloaded and imported at the recommended level (Max), every layer the import window offers, unattended; and a 2.x bundle imported.

  - Revit 2025, `revit.plugin-loads`: pass, 8 of 8
  - Revit 2025, `revit.plugin-windows`: pass, 50 of 50
  - Revit 2025, `revit.import-check`, bundle 3.0.0 level Max: pass, 40 of 40
  - Revit 2025, `revit.import-check`, bundle 2.2.0: pass, 18 of 18
  - Revit 2026, `revit.plugin-loads`: pass, 8 of 8
  - Revit 2026, `revit.plugin-windows`: pass, 50 of 50
  - Revit 2026, `revit.import-check`, bundle 3.0.0 level Max: pass, 40 of 40
  - Revit 2026, `revit.import-check`, bundle 2.2.0: pass, 18 of 18

  A run left on the recommended level makes 40 assertions where a run at a named level makes 41: naming a level also asserts that the order offers it. The first 3.0 run on Revit 2026 was ended by the harness at 28 of 29: it read Revit's own Model Upgrade progress window, shown for a moment while the planting family loads, as a dialog waiting for a person. The import itself went on to finish, and the same run again, unchanged, is the pass above.

- Not run: the ribbon's commands other than Vault, Import Bundle and About. Sign In, Sign Out, Open mantle.place, Send Feedback, Prepare for Handover, Open in Curator, Probe Terrain and Logs are on the ribbon in each year, each with its help page, and the gate ran none of them. On Revit 2025 and 2026, Med and Min and a send from the web vault (run on Revit 2027 alone); cancelling a sent bundle; a 2.x bundle beside an earlier import; roads, road surfaces, water, land use and land cover from a 3.0 order (the order holds none of those files, so each step was shown as skipped).
- Published asset: <check from step 4 and its date>.
- `MantlePlace-Revit-1.2.0.zip` sha256 `1fe84c327460c35ff64f973cb32a205192a986b7d43908ed2dde0b6519271974`
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

A 3.0 bundle imports from the vault, from a zip on disk and from a send out of the web vault, by the same rules
as a 2.x bundle. The project's shared coordinates are turned by the grid rotation the manifest's local frame
states, verbatim. That rotation is the grid's angle to true north at the site, which is not zero for a real
order, and the plugin never places a bundle on a rotated grid beside an earlier import: so a project that already
holds an import refuses a 3.0 bundle, the same order again included, and is left unchanged. Which angle a 3.x
host applies is an open question of the format ([677](https://github.com/mantleplace/mantleplace-dcc/issues/677));
until it is settled, a 3.0 order goes into a project that holds no earlier import. A layer file whose catalog entry names its tables is read
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
