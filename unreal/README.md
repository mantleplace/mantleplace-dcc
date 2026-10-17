# Mantle Place for Unreal

The Unreal plugin — host #1, and the reference host. One `.uplugin` with two modules,
`MantlePlaceRuntime` and `MantlePlaceEditor`, for **Unreal Engine 5.8**, editor-side. Its engine
dependencies are Epic's built-in Interchange and PCG (the foliage scatter).

**Status:** pre-1.0. Releases are tagged `unreal-<version>`
([ADR 0001](../docs/adr/0001-per-host-release-tracks.md)); the version lives in
[`MantlePlace.uplugin`](MantlePlace/MantlePlace.uplugin) and nowhere else.

## What it does today

One import produces **one `ALandscape`** with the bundle's imagery draped onto its geographic
footprint, plus whatever else the bundle ships: the buildings as a static mesh, the road centrelines
as one typed spline actor per road, the coverage rasters (water, land cover and their like) as data
textures with their value mappings attached, and the landcover material weights painted as landscape
layers. The tree points land as a data table, PCG-ready scatter input, and on request (a project setting) as a PCG foliage scatter that keeps off water, follows the canopy height and streams per World Partition cell. The public-record layers a bundle carries
(parcels, zoning, flood zones and the families that follow them) are credited in the import log, and their
DXF linework is drawn as one toggleable overlay per family where the file states a frame that is this
host's. Importing a local bundle
needs no account and no sign-in.

What this plugin does not do yet, and what it leaves out on purpose, is the
[host support matrix](../docs/host-support.md) — one row per feature, beside every other host.

## Install

1. Download `MantlePlace-UE5.8-Win64-<version>.zip` from the newest Unreal release on the
   [Releases page](https://github.com/mantleplace/mantleplace-dcc/releases).
2. Extract it into your project's `Plugins/` folder (create the folder if the project has none).
3. Open the project. If the editor asks to enable the plugin, accept, then restart when prompted.

The prebuilt binary is **Windows only** today. Any other platform, or any other engine version, is a
source build: see [Building](../README.md#building) in the root README.

## Use it

**Window ▸ Mantle Place** opens the panel. From there either:

- **Sign In** — your system browser opens; there is no token to paste. Once signed in the panel
  lists the bundles you own; pick one and import it.
- **Browse...** — pick a bundle `.zip` you downloaded from [mantle.place](https://mantle.place),
  leave the mode at *Landscape*, and press **Import**.

## Save your work

**The importer saves nothing.** Everything it creates — the assets and the actors in your level — is
only marked dirty. Use **File ▸ Save All** (Ctrl+Shift+S) before you close the editor, or the import
is gone.

## Where content lands

Assets go under `/Game/MantlePlace/<identity>/` in `Imagery`, `Mesh`, `Buildings`,
`CoverageRasters` and `Landcover`; actors go into the outliner folder `MantlePlace/<identity>`. The
identity names the *order*, so importing the same order again **replaces** the previous content
rather than adding a second copy ([ADR 0002](../docs/adr/0002-import-identity.md)). The content
root is a project setting — Project Settings ▸ Plugins ▸ Mantle Place ▸ *Generated content root* —
the outliner folder is fixed.

## Troubleshooting

- **Output Log**: filter on `LogMantlePlaceImport` (the import itself), `LogMantlePlaceAuth`
  (sign-in) and `LogMantlePlaceDrape` (the imagery material). Filtering on `LogMantlePlace` catches
  every category the plugin writes.
- **A re-import is refused** naming a folder: the importer keeps a provenance record per order under
  `Saved/MantlePlace/Imports/`, and refuses to delete content it cannot match to one — including
  content imported by 0.3.0 or earlier. The message names the path; move or delete that content by
  hand and import again.
- **Content lands somewhere unexpected**: check *Generated content root* in the project setting
  above. An unusable value falls back to the default rather than failing the import.

## Stream to compare

The plugin can also stream the *same* bundle through
[Cesium for Unreal](https://cesium.com/platform/cesium-for-unreal/) beside the imported copy, from a
local loopback tile server — nothing is streamed from the platform. It needs Cesium for Unreal
installed and enabled in the project (validated against 2.22.1) and is driven from the editor's
Python console, per
[`mantleplace_cesium_stream.py`](MantlePlace/Content/Python/mantleplace_cesium_stream.py):

```python
import mantleplace_cesium_stream as mp
mp.stream_into_cesium()                             # newest cached vault bundle (the one just imported)
```

## Packaging a release

Packaging needs a licensed Unreal Engine 5.8 install, so it runs privately and never in public CI
([ADR 0001](../docs/adr/0001-per-host-release-tracks.md)). A release is cut from a merged commit on
`main` whose `.uplugin` already carries the new version:

1. **Bump the descriptor** in its own pull request — `Version` and `VersionName` in
   [`MantlePlace.uplugin`](MantlePlace/MantlePlace.uplugin), the only place the version lives.
2. **Build and run the suite** at the merged commit (the command is in [`CLAUDE.md`](CLAUDE.md#commands)),
   and run the naming and conformance gates beside it.
3. **Export, then build from the export.** Build from a short path, not from a checkout: Unreal's
   build tool fails past Windows' 260-character path limit. The tree carries its own
   `Config/FilterPlugin.ini`, which names the two root files the package carries.

   ```powershell
   git archive --format=zip -o D:\mp-pkg\src.zip <sha> unreal/MantlePlace LICENSE TRADEMARK.md
   Expand-Archive D:\mp-pkg\src.zip D:\mp-pkg\src
   & "<UE_5.8>\Engine\Build\BatchFiles\RunUAT.bat" BuildPlugin `
       -Plugin=D:\mp-pkg\src\unreal\MantlePlace\MantlePlace.uplugin `
       -Package=D:\mp-pkg\out\MantlePlace -TargetPlatforms=Win64 -Rocket
   ```

4. **Assemble the zip.** Delete `Intermediate/` and every `*.pdb` from the package, copy `LICENSE`
   and `TRADEMARK.md` into it, and zip it with `MantlePlace/` at the root as
   `MantlePlace-UE5.8-Win64-<version>.zip`. Diffing its file list against the previous release's
   asset is a cheap sanity check.
   **The Fab upload is source only**: Epic compiles it. Zip the exported `unreal/MantlePlace/` with
   `LICENSE` and `TRADEMARK.md` copied into it, with no `Binaries/` or `Intermediate/`, as
   `MantlePlace-UE5.8-Source-<version>.zip`.
5. **Compile it in a consuming project.** Point a consuming project's submodule at the same commit
   and let that project's own compile and live-import check run; the local suite skips the live
   import, so this is the gate that exercises a real bundle.
6. **Publish** the tag `unreal-<version>` at that commit, with the zip as its asset, titled and
   written to [`docs/releases.md`](../docs/releases.md).

For anything deeper — building, testing, naming, what a change here must not do — read
[`CLAUDE.md`](CLAUDE.md) in this folder and the root [`README.md`](../README.md).
