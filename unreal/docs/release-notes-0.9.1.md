# Unreal 0.9.1: release notes

The body the `unreal-0.9.1` release takes, titled `unreal-0.9.1 — Mantle Place for Unreal` and written to
[`docs/releases.md`](../../docs/releases.md), then the detail behind its one fix. Everything else is as in
0.9.0, whose notes ([`release-notes-0.9.0.md`](release-notes-0.9.0.md)) hold what an MPB 3.0 bundle imports
and what it leaves out. The **Verification** section holds the release gate's results on the packaged zip; its
published-asset and storefront lines are filled in when the release is cut and the zip is submitted. The
published body cites each pull request by its bare number, as [`docs/releases.md`](../../docs/releases.md)
asks; this file links each number instead, because a bare number in a tracked file is refused by the
public-reference gate.

## The release body

```markdown
**Unreal Engine 5.8, Windows 64-bit.** Extract the zip into your project's `Plugins/` folder and open the project. [Install guide](https://github.com/mantleplace/mantleplace-dcc/blob/main/unreal/README.md#install)
Upgrade by replacing the `MantlePlace` folder.

## Upgrading from 0.9.0

- **Download an MPB 3.0 order again to get its public-record layers.** A level downloaded by 0.9.0 holds none, and importing the same level again reuses that download. Download another level once, then the first again if you want it: each change of level is a fresh download, and it holds the layers.

## What's new

- **An MPB 3.0 order imported from the vault draws its public-record layers.** In 0.9.0 the level download left the record linework out, so only a zip on disk or a bundle sent from the web vault drew them. The download now asks for each record layer the order holds ([720](https://github.com/mantleplace/mantleplace-dcc/pull/720)).

## Known issues

- **A public-record layer shows no per-layer disclaimer.** The MPB 3.0 manifest does not carry one yet; the layer's credit and the standing notice are shown.
- **The import log names a GeoPackage as not in the download for a record layer that was drawn.** The level download asks for the DXF linework only, and the layer is complete without the GeoPackage.
- **The habitat record layer is not in a level download.** The published Platform API description does not list it yet; a zip on disk that holds it draws it.

## Verification

- Built from `24be1a9` on `main` (`git archive` of that commit, then `RunUAT BuildPlugin`, `Intermediate/` and every `*.pdb` removed, `LICENSE` and `TRADEMARK.md` added; 248 files). Its Source also builds under a forced unity compile (`UnrealBuildTool UnrealEditor Win64 Development -DisableAdaptiveUnity`, Succeeded). Gate result at that commit, on the Win64 zip installed clean in a fresh Unreal 5.8 project and signed in on the dev platform, six runs one at a time against one dev-built 3.0.0 order (Max, Med, Min and the whole bundle, its imagery draped at every level) and one 2.2.0 order: the automation suite on the whole 3.0 bundle (all 106 automation tests), the live import at each 3.0 level and of the 2.x order, and a bundle sent from the web vault and imported.

  - `unreal.automation-suite v1: pass, 4 of 4 assertions, unreal 5.8, bundle 3.0.0 level Max (named zip), plugin 0.9.1 (release zip MantlePlace-UE5.8-Win64-0.9.1.zip, sha256 a645f0badcc5), harness 0.17.0, 2 artefacts`
  - `unreal.import-check v2: pass, 17 of 17 assertions, unreal 5.8, bundle 3.0.0 level Max (vault order), plugin 0.9.1 (release zip MantlePlace-UE5.8-Win64-0.9.1.zip, sha256 a645f0badcc5), harness 0.17.0, 3 artefacts`
  - `unreal.import-check v2: pass, 17 of 17 assertions, unreal 5.8, bundle 3.0.0 level Med (vault order), plugin 0.9.1 (release zip MantlePlace-UE5.8-Win64-0.9.1.zip, sha256 a645f0badcc5), harness 0.17.0, 3 artefacts`
  - `unreal.import-check v2: pass, 17 of 17 assertions, unreal 5.8, bundle 3.0.0 level Min (vault order), plugin 0.9.1 (release zip MantlePlace-UE5.8-Win64-0.9.1.zip, sha256 a645f0badcc5), harness 0.17.0, 3 artefacts`
  - `unreal.import-check v2: pass, 16 of 16 assertions, unreal 5.8, bundle 2.2.0 (vault order), plugin 0.9.1 (release zip MantlePlace-UE5.8-Win64-0.9.1.zip, sha256 a645f0badcc5), harness 0.17.0, 3 artefacts`
  - `vault.round-trip v1: pass, 14 of 14 assertions, vault unreal-5.8, plugin 0.9.1 (release zip MantlePlace-UE5.8-Win64-0.9.1.zip, sha256 a645f0badcc5), harness 0.17.0, 3 artefacts`

- Published asset: <check from step 4 and its date>.
- `MantlePlace-UE5.8-Win64-0.9.1.zip` sha256 `a645f0badcc523dc29b7e6ad5f10d4398f390ba98434f2ef8a25ca3633b14585`
- Storefront: <submitted to Fab on date>, from the Source zip built from the same commit (`MantlePlace-UE5.8-Source-0.9.1.zip`, sha256 `c362c413f680d7855a3210467b258424ea613d8aec0fb615b29e1dbc99c911d9`).

Previous: [unreal-0.9.0](https://github.com/mantleplace/mantleplace-dcc/releases/tag/unreal-0.9.0)
```

## The public-record layers in a level download

A level download of an MPB 3.0 order names the files the import reads. From 0.9.1 that list holds the
linework of each public-record layer the published Platform API description lists (`Records/<Name>.dxf`:
Parcels, Zoning, Floodplain, Wetlands, Soils, Heritage, Conservation, Hydrography, Contamination). A layer
the order does not hold is answered as not in the bundle, the plugin asks again without it, and the import
window lists it as not in this download with the platform's reason. The GeoPackage twins are not asked
for: the plugin verifies and credits a GeoPackage it is given and draws only the DXF.

## A correction to the 0.9.0 notes: the tree points arrive thinned

The 0.9.0 notes say the foliage scatter of a 3.0 import "runs without the canopy-height, water and
land-cover filters". The plugin does not apply those filters to a 3.0 bundle, and it does not need to: the
platform thins `Landcover/Trees.csv` by canopy, water and land cover before it ships
([`spec/format.md`](../../spec/format.md)), so the points a 3.0 import scatters are already filtered.
