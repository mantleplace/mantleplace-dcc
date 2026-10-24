---
name: unreal-host-onboarding
description: Onboarding for the Unreal plugin — UE 5.8, Interchange, PCG and SQLiteCore, the two modules, how to drive the headless automation tests, the generated-name standard and where imported content lands, and the traps (CI never compiles this plugin, nothing the importer generates is saved, the identity is not the job id). An index — the detail is in `unreal/docs/`. Read first for any change under `unreal/`.
---

# Mantle Place for Unreal — agent onboarding

Read the repo root [`CLAUDE.md`](../CLAUDE.md) first. This file is an index: each rule is stated
here in a line, and its detail lives in the document it links.

## Identity

- **Host:** Unreal Engine **5.8**, editor-side. The plugin folder plus its `.uplugin` is
  [`MantlePlace/`](MantlePlace/) — PascalCase within; a consuming project's plugin discovery is a
  recursive scan of `Plugins/`.
- **Engine dependencies:** Epic's built-in **Interchange**, **PCG** (the foliage scatter's graph
  and its partitioned generation) and **SQLiteCore** (the road splines' GeoPackage; SQLite is read
  through the engine's module, never a vendored copy), and nothing else. A new engine plugin dependency is a new thing a
  stranger's project has to have enabled. Mesh Partition (UE 5.8's
  Experimental Mesh Terrain) is deliberately not one
  ([ADR 0016](../docs/adr/0016-unreal-mesh-terrain-is-parked-until-5-9.md)).
- **Modules:** `MantlePlaceRuntime` (Runtime) and `MantlePlaceEditor` (Editor). The version lives in
  the `.uplugin` and nowhere else.
- **Frame:** Unreal is a **fixed-frame host** (`HPS-54`) — one native unit, so its frame does not
  move with the order's unit system. Its content, and the origin it is placed against, are **metric
  UTM on every order, an imperial one included**; a file stated on another grid is refused rather
  than converted (`HPS-53`).
- **Role:** host #1, the reference host — where a rule was written with Unreal in mind, this tree
  shows what it meant, so an expedient shortcut here costs more than elsewhere.
- **Releases:** their own track, tagged `unreal-<version>` with no `v`
  ([ADR 0001](../docs/adr/0001-per-host-release-tracks.md)); the release body is the changelog.
  `v0.1.0`–`v0.3.0` are this track's pre-history, never renamed. Packaging needs an engine install,
  so it runs privately.

## The standard binds this folder

The Host Plugin Standard is normative and cited by `HPS-NN` id in the code. What the rules a patch
here is most likely to break mean here is [`docs/standard-map.md`](docs/standard-map.md): `HPS-32`
the band legend is data · `HPS-33` verbatim, a paint layer's name included · `HPS-46` corpus keys
asserted · `HPS-51` the panel takes the standard's words · `HPS-52` the `unreal` block first ·
`HPS-53` a placed file is shown to be in this frame, and a refusal is a **named** skip (one open
deviation: the road-spline reader still drops a single refused point unnamed).

Root `CLAUDE.md`'s boundary is the one to internalise: **this plugin applies pre-derived values and
never derives them.** A patch that computes a placement value locally is refused even when the
arithmetic is correct.

## Layout, and where logic goes

```
MantlePlace/Source/MantlePlaceRuntime/   auth, vault client, bundle cache, sha256. No editor, no UI.
MantlePlace/Source/MantlePlaceEditor/    the importer, the vault panel, the naming module.
MantlePlace/Source/*/Private/Tests/      headless automation tests over the pure logic.
MantlePlace/Content/                     shipped assets: the drape material, the auth Blueprint base.
MantlePlace/Content/Python/              the Cesium streaming helper. Spawns actors; imports no assets.
```

**Put the decision in a `*Logic` translation unit** (`MantlePlace<Name>Logic.cpp`, one per
decision), each with a headless test beside it. The importer *executes*; the Logic unit *decides*.
If asserting it needs a running editor and a real bundle, it is in the wrong unit: logic in the
importer is covered by review alone.

**The importer is a registry of per-artifact handlers.** `ImportVaultPackage` runs the steps in
`MantlePlaceImportOrchestration` (verify, the nothing-to-import gate, the wipe, `Prepare`, `Import`,
`Finalize`) over the list in `MantlePlaceArtifactRegistry.cpp`; each artifact type is one
`MantlePlaceArtifact<Name>.cpp` that reads its own manifest block, declares its payloads for the
integrity chain, and imports them. **Adding an artifact type** is exactly: one handler file, one
sub-struct in `MantlePlaceManifestBlocks.h` held as a member of `FMantlePlaceVaultManifest`, and one
entry in `MakeAll`. `Parse`, the integrity chain and the orchestration do not change. The registry is
an explicit ordered list on purpose: no discovery, no reflection.

## Reading a bundle: 1.x and 2.0, by pointer, regardless of case

The importer reads MPB 1.x and 2.0 (`MantlePlaceMaxSupportedManifestMajor`; the floor stays 1.0.0) with
one parse. A file is found through the path its manifest pointer states, never from a folder name: the
only fixed name is `Metadata/manifest.json`. 2.0 moved the folders and the names, and it moved where a
PNG's pixel meaning is stated: a 1.x host block carries `uint16_mapping` and `ue_ready[].value_mapping`;
a 2.0 bundle states it only in `file_entries[<path>].value_mapping`. A host-block statement is applied
verbatim and wins, and a block without one reads the file's own entry.

A manifest path matches an archive entry **regardless of case** (format.md 10.3 rule 6). **The engine's
zip reader already does:** `FZipArchiveReader` keeps its entries in a `TMap<FString, ...>` whose hash and
key comparison ignore case, so `metadata/MANIFEST.json` finds `Metadata/manifest.json`; checked against
the UE 5.8 source and by `MantlePlace.Import.BundleLayout`, which writes a real zip in another case than
its pointers. Python's `zipfile` does not: the Cesium QA helper finds its manifest member through
`_find_member`, and `unreal/tools/test_cesium_stream_manifest.py` pins it. Wherever the importer
compares a manifest path itself (a `file_entries` key, the streaming selection), it ignores case too.

## Commands

The tests are Unreal automation tests under a `MantlePlace.` prefix. Run the whole suite with the
full editor, offscreen, opened on a `/Game` partitioned World Partition level:

```
UnrealEditor.exe <scratch>.uproject /Game/<a partitioned World Partition level> -ExecCmds="Automation RunTests MantlePlace." -unattended -nopause -nosplash -RenderOffscreen -testexit="Automation Test Queue Empty" -log
```

Not `UnrealEditor-Cmd.exe`, and not `-nullrhi` (it crashes in the engine's HLOD build). The
editor-world tests (`WorldPartition.DataLayers`, `WorldPartition.FoliageScatter`, the foliage
re-import test and others) skip with `No editor: ... needs an editor world. Skipped.` whenever the
startup level is not a partitioned World Partition level, and a skipped run still reports green. **A
run reporting any `No editor` skip is not green.**

- **Corpus.** The conformance tests walk up (at most eight levels) from the plugin directory looking
  for `tools/manifest-conformance/corpus/index.json`
  (`FindCorpusDir` in `MantlePlaceConformanceCorpus.h`) and fail if they do not find it. A scratch
  project holding only `MantlePlace/` under `Plugins/` cannot reach it: place the scratch project (or
  a copy of the plugin) beneath a directory that has `tools/manifest-conformance/corpus/`, or copy
  that `corpus/` folder to `tools/manifest-conformance/corpus/` in a directory above the plugin.
- **`MantlePlace.Import.LiveFreeTier`** runs offline against a local bundle zip: set
  `MP_LIVETEST_BUNDLE` to it (otherwise it falls back to
  `<ProjectSaved>/MantlePlace/livetest/download.zip`). With no bundle, or one below the manifest
  floor, it warns and passes locally; it errors only when `CI` is set. A "Sample bundle not present"
  (or "below the floor") warning therefore means the run is not green, whatever the count says. Its
  placement gate checks the landscape against the bundle's own surface points (`elevation.points_csv`),
  which the importer never reads; a "Placement gate not run" warning is the same not-green. When the
  bundle's road layer is in the landscape's datum, it also checks the roads were read from the
  GeoPackage the layer declares (a 2.0 bundle declares no GeoJSON row) and that the median placed
  spline point is within a metre of the landscape. Open the run on an empty partitioned level
  (`new_level(..., is_partitioned_world=True)`), not the OpenWorld template: the template's own
  landscape can be the one the test picks up, and every gate then measures it.
  **`MantlePlace.Import.LiveHandover`** imports the same bundle, checks every ODbL actor is in its
  order's `ODbL` folder, then runs Prepare for handover and checks the ODbL content is gone and the
  credit actor holds every credit line the import recorded.
- **Scratch hygiene.** Set `EditorStartupMap` and `GameDefaultMap` to a `/Game` map or
  `/Engine/Maps/Templates/Template_Default`, never `/Engine/Maps/Entry`; never save an `/Engine`
  package (one such save overwrote an install's `Entry.umap` and broke other projects' tests). In Git
  Bash set `MSYS_NO_PATHCONV=1` when you pass `/Game` paths.

**To compile a branch**, use a scratch project outside every repository, with this
worktree's `MantlePlace/` in its `Plugins/` and one C++ module, so it has an Editor target (never
the install slot below):
`Engine/Build/BatchFiles/Build.bat <Name>Editor Win64 Development -Project=<scratch>.uproject -WaitMutex -NoHotReloadFromIDE`.
The last flag gets past an open editor's Live Coding refusal. A syntax-only pass is not a compile.

The contract gate and the name-drift gate (text-only, no engine) run before you push:
[`docs/agents/ci.md` ▸ Running the gates locally](../docs/agents/ci.md#running-the-gates-locally).

## Naming

Two rules meet here; conflating them is the mistake. **Root `CLAUDE.md`'s "`mantleplace`, never
`mp`" governs the repository and the code** — paths, folders, modules, classes, C++ symbols — not
strings the plugin writes into a user's project. **Inside a user's project, `MP_` is the permitted
marker on outliner-visible actor labels, and only there**
([ADR 0003](../docs/adr/0003-naming-authority-and-mp-prefix.md)): a label is read in a cramped
outliner column, reappears unqualified in Details, logs and Blueprint references, and is the only
marker that survives a reconfigured content root.

**A third kind: the words on the panel's own controls** — the auth button, the list heading, the
local-import section. They are fixed cross-host by `HPS-51`'s table; the editor's casing and the
panel's layout stay ours, and a host construct keeps its noun (`Landscape` here, `Toposolid` there).
They live inline in `SMantlePlaceVaultPanel.cpp`, which CI never compiles, so review reads the table.

### Generated assets

Asset names take a type prefix. This table is the standard — reproduced here rather than deferred to
a document a reader of this repository cannot open:

| Prefix  | Asset type                   |
| ------- | ---------------------------- |
| `SM_`   | Static mesh                  |
| `T_`    | Texture                      |
| `M_`    | Material                     |
| `MI_`   | Material instance (constant) |
| `LI_`   | Landscape layer info         |
| `DT_`   | Data table                   |
| `DL_`   | Data Layer                   |
| `HLOD_` | HLOD layer                   |
| `PCG_`  | PCG graph                    |
| `BP_`   | Blueprint                    |

An imported asset is *given* its name so it lands conforming; renaming inside the import
transaction is the hazardous path, a fallback only for assets a glTF brings with it.

The rest is [`docs/generated-content.md`](docs/generated-content.md):

- **Where content lands** — under the order's identity
  ([ADR 0002](../docs/adr/0002-import-identity.md)), in a content root set per project.
- **Paint layers are not ours to name** — the layer's name is the platform's material name verbatim
  (`HPS-33`); only the asset is `LI_<material>`.
- **One place, not every call site** — every generated name and path comes from
  `MantlePlaceImportNaming`; `MantlePlace.Import.Naming` and the drift gate check it, and a line that
  needs an exception carries `// naming-gate: allow <reason>`.
- **Two imported artifacts are typed, with a documented contract** — a road is an `AMantlePlaceRoadSpline`
  whose class, name and width are properties ([`docs/road-spline-actor-contract.md`](docs/road-spline-actor-contract.md)),
  never tag strings; the tree points are a table whose property names are the columns a PCG graph binds
  by ([`docs/foliage-table-contract.md`](docs/foliage-table-contract.md)). Both list what the platform
  does not publish yet, which is a manifest request and never a local derivation.
- **Re-import replaces, behind two guards** — a provenance record under `Saved` holding the full
  identity, and the `mantleplace_import=<identity>` actor tag the sweep matches.

## Third-party content

Third-party content in this plugin lives **only under `MantlePlace/Source/ThirdParty/`**, directly
under `Source/`: Fab's technical review requires it, and a plugin that ships any must also file Fab's
Third Party Software form. **Today there is none, on purpose**: the editor panel draws with the
engine's own typefaces (`FMantlePlaceEditorStyle::GetFontTypeface`), so no font file ships, no form
is owed, and `Resources/` holds only our own icons. Adding a third-party file brings the form back;
ask first. The gate
enforces it: `tools/unreal-naming/check_third_party_layout.py`, run by `ci-unreal-naming`, fails any
licence text (an `OFL*`, `LICENSE*`, `COPYING*` or `NOTICE*` file, or a text file naming the SIL Open
Font License, MIT, BSD or GPL) that sits in the plugin folder outside `Source/ThirdParty/`. The
Fab pre-submit list is in [`README.md`](README.md#packaging-a-release).

## Things that will bite you

- **CI never compiles this plugin** ([why](../docs/agents/ci.md)). Build it locally (the recipe is
  under Commands) before you claim it works, and distrust a type widening: it compiles at some
  call sites and silently rots others.
- **The editor compiles the consuming project's submodule checkout, not this tree** (`HPS-50`), a
  single slot that is `main` by default and moves only by `tools/Refresh-UnrealInstall.ps1`
  (`origin/main` after a merge, `origin/<branch>` for a preview you were asked for);
  `tools/Check-UnrealInstall.ps1` says which it holds. Both need `MANTLEPLACE_CONSUMING_PROJECT_ROOT`
  or `-ConsumingProjectRoot`, else `not configured` — which the session-start check treats as fine. Editing inside that checkout is root rule 3's
  orphan-commit hazard: edit in a worktree here, push, preview from origin. Live Coding picks up
  `.cpp` bodies; a changed header or asset needs a restart. The consumer documents its pin bump.
- **Nothing the importer generates is ever saved.** No `SavePackage`; every task sets
  `bSave = false` and packages are only marked dirty. Closing without saving loses the import.
- **The identity is not the job id.** `FMantlePlaceVaultManifest` offers `JobId` first; it changes
  on every rebuild, so anything keyed on it silently duplicates a user's content one refresh later.
  `MantlePlaceImportNaming::ResolveIdentity` is the only place that decides.
- **The actor tag is load-bearing; the label is not.** Change the label format freely; change the
  tag and you change which actors a re-import destroys.
- **Re-import wipes before it writes.** Anything that widens what the destination path can be widens
  what that delete can reach — guard the inputs to it, not the delete.
- **[`.clang-format`](.clang-format) is for new code only**; nothing checks it and a reformat sweep
  is refused ([CONTRIBUTING.md](../CONTRIBUTING.md)).
- **No credentials in a `.uasset`, ever** — a URL in a Blueprint's class defaults is in the file.

## Where knowledge lives

- The bundle-manifest contract → the published JSON Schema series at
  `https://mantle.place/.well-known/schemas/bundle-manifest/`; this host's verified version lives in
  [`verified-against.json`](../tools/manifest-conformance/verified-against.json), never in prose.
- What each `HPS-NN` means here → [`docs/standard-map.md`](docs/standard-map.md); the rules
  themselves → [`docs/host-plugin-standard.md`](../docs/host-plugin-standard.md).
- Signing in, tokens, refresh, sign-out →
  [`docs/platform-auth-contract.md`](../docs/platform-auth-contract.md), cross-host; both hosts share
  one stored credential.
- Shared vocabulary → [`CONTEXT.md`](../CONTEXT.md). Why a decision was taken →
  [`docs/adr/`](../docs/adr/).
- How this plugin behaves and how to build it → [`README.md`](../README.md).
- Whether this plugin supports a feature, and why not → the
  [host support matrix](../docs/host-support.md). A change that adds, narrows or drops a feature
  edits the **Unreal** column in the same pull request, and only that column; a gap links its issue
  or ADR. Never state a per-host status anywhere else.
