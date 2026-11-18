# Unreal 0.9.0: release notes

The body the `unreal-0.9.0` release takes, titled `unreal-0.9.0 — Mantle Place for Unreal` and written to
[`docs/releases.md`](../../docs/releases.md), then the detail behind its MPB 3.0 lines. A 2.x bundle imports exactly
as in 0.8.1. The **Verification** section holds the release gate's results on the packaged zip; its published-asset
and storefront lines are filled in when the release is cut and the zip is submitted. The published body cites each pull request by its bare number, as
[`docs/releases.md`](../../docs/releases.md) asks; this file links each number instead, because a bare number in a
tracked file is refused by the public-reference gate.

## The release body

```markdown
**Unreal Engine 5.8, Windows 64-bit.** Extract the zip into your project's `Plugins/` folder and open the project. [Install guide](https://github.com/mantleplace/mantleplace-dcc/blob/main/unreal/README.md#install)
Upgrade by replacing the `MantlePlace` folder; see below for what an MPB 3.0 order changes.

## Upgrading from 0.8.1

- **An MPB 3.0 order is downloaded at one level.** The import window asks for Max, Med or Min before the download, with the platform's recommendation preselected. A 2.x order downloads as before.
- **Some 2.x layers are not part of an MPB 3.0 import.** The coverage rasters (slope, aspect, hillshade, land cover, canopy height, NDVI) are skipped, the water mask is painted as the `Water` layer instead, and the foliage scatter runs without the canopy-height, water and land-cover filters those rasters fed. Each skip is named, with its reason, in the import window and the import log.
- **Streaming a 3.0 order into Cesium needs a signed-in session the first time.** Its terrain tiles are fetched beside the bundle on the first stream.

## What's new

- **MPB 3.0 bundles import.** Landscape, paint layers, imagery drape, terrain mesh, buildings, tree points and road splines, from the vault or a zip on disk, in each import mode ([657](https://github.com/mantleplace/mantleplace-dcc/pull/657), [663](https://github.com/mantleplace/mantleplace-dcc/pull/663)).
- **Choose the level before the download.** Max, Med or Min, each with its download size; importing the same level again reuses the download ([666](https://github.com/mantleplace/mantleplace-dcc/pull/666)).
- **Editor scripting imports a 3.0 order at a named level**, from Blueprint or editor Python ([688](https://github.com/mantleplace/mantleplace-dcc/pull/688)).
- **Public-record layers are drawn on the Landscape.** Each layer is one toggleable overlay of lines, area outlines and point markers, with its sources and licence as its credit ([664](https://github.com/mantleplace/mantleplace-dcc/pull/664), [683](https://github.com/mantleplace/mantleplace-dcc/pull/683)).
- **Streaming a 3.0 order into Cesium** fetches the order's terrain tiles and keeps them beside the bundle ([674](https://github.com/mantleplace/mantleplace-dcc/pull/674)).
- **A Bundle Import window** lists every step as it runs and can stop between steps ([631](https://github.com/mantleplace/mantleplace-dcc/pull/631)).
- **A second bundle is placed beside the one already in the level** ([574](https://github.com/mantleplace/mantleplace-dcc/pull/574)).
- **Open in curator** opens the web curator on the extent the level already knows, from the panel or from editor Python ([526](https://github.com/mantleplace/mantleplace-dcc/pull/526), [553](https://github.com/mantleplace/mantleplace-dcc/pull/553)).
- **The plugin tells you when the platform needs a newer version**, instead of failing the sign-in or the download ([523](https://github.com/mantleplace/mantleplace-dcc/pull/523)).
- **"Did this import come in right?"** After an import, a Yes or No opens the feedback form for that bundle ([500](https://github.com/mantleplace/mantleplace-dcc/pull/500)).
- **Imported terrain collides where it is drawn**, in every import mode ([701](https://github.com/mantleplace/mantleplace-dcc/pull/701)).
- **Fixes for 3.0 bundles:** a heightmap whose unit is stated as `m` ([675](https://github.com/mantleplace/mantleplace-dcc/pull/675)), a road table with no name column ([682](https://github.com/mantleplace/mantleplace-dcc/pull/682)), and tree points that lie on the Landscape's edge ([686](https://github.com/mantleplace/mantleplace-dcc/pull/686)).

## Known issues

- **A public-record layer shows no per-layer disclaimer.** The MPB 3.0 manifest does not carry one yet; the layer's credit and the standing notice are shown.

## Verification

- Built from `44b2447` on `main` (`git archive` of that commit, then `RunUAT BuildPlugin`, `Intermediate/` and every `*.pdb` removed, `LICENSE` and `TRADEMARK.md` added; 245 files). Gate result at that commit, on the Win64 zip installed clean in a fresh Unreal 5.8 project and signed in on the dev platform, six runs one at a time against one dev-built 3.0.0 order (Max, Med, Min and the whole bundle, its imagery draped at every level) and one 2.2.0 order: the automation suite on the whole 3.0 bundle (103 of 104 automation tests: `MantlePlace.Import.LiveTerrainCollision` hit 621 of 622 building-roof traces in each mode; the terrain collided everywhere), the live import at each 3.0 level and of the 2.x order, and a bundle sent from the web vault and imported.

  - `unreal.automation-suite v1: fail, 3 of 4 assertions, unreal 5.8, bundle 3.0.0 level Max (named zip), plugin 0.9.0 (release zip MantlePlace-UE5.8-Win64-0.9.0.zip, sha256 fbd3d644203d), harness 0.17.0, 2 artefacts`
  - `unreal.import-check v2: pass, 17 of 17 assertions, unreal 5.8, bundle 3.0.0 level Max (vault order), plugin 0.9.0 (release zip MantlePlace-UE5.8-Win64-0.9.0.zip, sha256 fbd3d644203d), harness 0.16.0, 3 artefacts`
  - `unreal.import-check v2: pass, 17 of 17 assertions, unreal 5.8, bundle 3.0.0 level Med (vault order), plugin 0.9.0 (release zip MantlePlace-UE5.8-Win64-0.9.0.zip, sha256 fbd3d644203d), harness 0.16.0, 3 artefacts`
  - `unreal.import-check v2: pass, 17 of 17 assertions, unreal 5.8, bundle 3.0.0 level Min (vault order), plugin 0.9.0 (release zip MantlePlace-UE5.8-Win64-0.9.0.zip, sha256 fbd3d644203d), harness 0.16.0, 3 artefacts`
  - `unreal.import-check v2: pass, 16 of 16 assertions, unreal 5.8, bundle 2.2.0 (vault order), plugin 0.9.0 (release zip MantlePlace-UE5.8-Win64-0.9.0.zip, sha256 fbd3d644203d), harness 0.16.0, 3 artefacts`
  - `vault.round-trip v1: pass, 14 of 14 assertions, vault unreal-5.8, plugin 0.9.0 (release zip MantlePlace-UE5.8-Win64-0.9.0.zip, sha256 fbd3d644203d), harness 0.17.0, 3 artefacts`

- Published asset: <check from step 4 and its date>.
- `MantlePlace-UE5.8-Win64-0.9.0.zip` sha256 `fbd3d644203dd8451f0fb0d05915a42540efe4fd4843de4fdf4f796ebefa87c8`
- Storefront: <submitted to Fab on date>, from the Source zip built from the same commit (`MantlePlace-UE5.8-Source-0.9.0.zip`, sha256 `8f7f815a7e398970ea0b43615c7c2a2f73b308fcde923536e100bbeadafdd628`).

Previous: [unreal-0.8.1](https://github.com/mantleplace/mantleplace-dcc/releases/tag/unreal-0.8.1)
```

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
it. A script names the level with `StartVaultImportAtLevel` (Blueprint and editor Python), and one that names
none takes the recommendation. A 2.x order downloads as in 0.8.1, with no level to choose.

## Every 2.x item an MPB 3.0 import leaves out

What a 2.x bundle brought into the level that a 3.0 bundle does not, and why. Every other 2.x step has its 3.0 file in
the table above. Each skip is shown, with its reason, in the import window and the import log.

| 2.x item | In a 3.0 import | Why |
| --- | --- | --- |
| Coverage rasters (slope, aspect, hillshade, land cover, canopy height, NDVI) | Skipped | MPB 3.0 ships them only as georeferenced GeoTIFFs, and the plugin never converts a georeferenced file. |
| Water mask | Painted as the `Water` paint layer | MPB 3.0 ships no separate mask: the water coverage is `Materials/Water.png`. |
| The foliage scatter's canopy-height, water and land-cover filters | The scatter runs unfiltered, and its log line names the filters it lacks | Those filters were read from the coverage rasters above. |
| Terrain tiles inside the bundle | Fetched from the order's terrain tiles Extra on the first stream | MPB 3.0 holds no terrain tiles; the platform lists them beside the bundle (below). |

## Streaming into Cesium

An MPB 3.0 bundle holds no terrain tiles: the platform lists them as an Extra beside the bundle. The first
stream of a downloaded 3.0 order fetches them (a signed-in session is needed), keeps them beside the bundle's
zip and streams when they arrive; a second stream reuses them. An order the platform lists no terrain tiles
for, or a zip from outside the plugin's download folder with none beside it, is refused with the reason. A 2.x
bundle streams its own tiles, as in 0.8.1.
