# Mantle Place for Unreal Engine

The Unreal plugin — host #1, and the reference host. One `.uplugin` with two modules,
`MantlePlaceRuntime` and `MantlePlaceEditor`, for **Unreal Engine 5.8**, editor-side. Its only engine
dependency is Epic's built-in Interchange.

**Status:** pre-1.0. Releases are tagged `unreal-<version>`
([ADR 0001](../docs/adr/0001-per-host-release-tracks.md)); the version lives in
[`MantlePlace.uplugin`](MantlePlace/MantlePlace.uplugin) and nowhere else.

## What it does today

One import produces **one `ALandscape`** with the bundle's imagery draped onto its geographic
footprint, plus whatever else the bundle ships: the buildings as a static mesh, the road centrelines
as one spline actor per road, the coverage rasters (water, land cover and their like) as data
textures with their value mappings attached, and the landcover material weights painted as landscape
layers. Two honest gaps: the weight layers are painted but the shipped drape material does not
render them yet, and the tree points land as a data table (PCG-ready scatter input) with no foliage
placed. Importing a local bundle needs no account and no sign-in.

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

For anything deeper — building, testing, naming, what a change here must not do — read
[`CLAUDE.md`](CLAUDE.md) in this folder and the root [`README.md`](../README.md).
