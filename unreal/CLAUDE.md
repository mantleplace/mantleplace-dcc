---
name: unreal-host-onboarding
description: Onboarding for the Unreal plugin — UE 5.8 and Interchange, the two modules, how to drive the headless automation tests, the generated-name standard and where imported content lands, and the traps (CI never compiles this plugin, nothing the importer generates is saved, the identity is not the job id). An index — the detail is in `unreal/docs/`. Read first for any change under `unreal/`.
---

# Mantle Place for Unreal — agent onboarding

Read the repo root [`CLAUDE.md`](../CLAUDE.md) first. This file is an index: each rule is stated
here in a line, and its detail lives in the document it links.

## Identity

- **Host:** Unreal Engine **5.8**, editor-side. The plugin folder plus its `.uplugin` is
  [`MantlePlace/`](MantlePlace/) — PascalCase within; a consuming project's plugin discovery is a
  recursive scan of `Plugins/`.
- **Engine dependency:** Epic's built-in **Interchange**, and nothing else. A new engine plugin
  dependency is a new thing a stranger's project has to have enabled.
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

**Put the decision in a `*Logic` translation unit** — `MantlePlaceLandscapeWeightsLogic`,
`MantlePlaceCoverageRasterLogic`, `MantlePlaceRoadSplinesLogic`, `MantlePlaceTreePointsLogic`,
`MantlePlaceDrapeAlignmentLogic`, `MantlePlaceDeliveryLogic`, `MantlePlaceIntegrityLogic`,
`MantlePlaceVaultLogic`, `MantlePlaceAuthLogic`, `MantlePlaceBundleCacheLogic`, each with a headless
test beside it. The importer *executes*; the Logic unit *decides*. If asserting it needs a running
editor and a real bundle, it is in the wrong unit — and since **CI never compiles this plugin**,
logic in the importer is covered by review alone.

## Commands

The tests are Unreal automation tests under a `MantlePlace.` prefix; drive them unattended with:

```
UnrealEditor-Cmd.exe <YourProject>.uproject -ExecCmds="Automation RunTests MantlePlace." -unattended -nopause -nosplash -testexit="Automation Test Queue Empty" -log
```

`MantlePlace.Import.LiveFreeTier` wants a real bundle and credentials, so it is not part of an
offline sweep.

```bash
python ../tools/manifest-conformance/check_manifest_conformance.py   # contract gate, offline corpus half
python ../tools/unreal-naming/check_generated_names.py              # name drift gate, no engine — run before you push
```

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

| Prefix | Asset type                   |
| ------ | ---------------------------- |
| `SM_`  | Static mesh                  |
| `T_`   | Texture                      |
| `M_`   | Material                     |
| `MI_`  | Material instance (constant) |
| `LI_`  | Landscape layer info         |
| `DT_`  | Data table                   |
| `BP_`  | Blueprint                    |

An imported asset is *given* its name so it lands conforming; renaming inside the import
transaction is the hazardous path, a fallback only for assets a glTF brings with it.

The rest is [`docs/generated-content.md`](docs/generated-content.md):

- **Where content lands** — `<ContentRoot>/<identity>/{Imagery,Mesh,Buildings,CoverageRasters,Landcover}`;
  the content root is a **project** setting, never per-user; the identity names the *order*
  ([ADR 0002](../docs/adr/0002-import-identity.md)) and does not repeat in leaf names; the outliner
  folder is fixed.
- **Paint layers are not ours to name** — the layer's name is the platform's material name verbatim
  (`HPS-33`); only the asset is `LI_<material>`.
- **One place, not every call site** — every generated name and path comes from
  `MantlePlaceImportNaming`; `MantlePlace.Import.Naming` and the drift gate check it, and a line that
  needs an exception carries `// naming-gate: allow <reason>`.
- **Re-import replaces, behind two guards** — a provenance record under `Saved` holding the full
  identity, and the `mantleplace_import=<identity>` actor tag the sweep matches.

## Things that will bite you

- **CI never compiles this plugin** — a licensed engine is needed, and a self-hosted runner would
  run a fork's code. Build it locally before you claim it works, and distrust a type widening: it
  compiles at some call sites and silently rots others.
- **The editor compiles the consuming project's submodule checkout, not this tree** (`HPS-50`), a
  single slot that is `main` by default and moves only by `tools/Refresh-UnrealInstall.ps1`
  (`origin/main` after a merge, `origin/<branch>` for a preview you were asked for);
  `tools/Check-UnrealInstall.ps1` says which it holds. Both need `MANTLEPLACE_CONSUMING_PROJECT_ROOT`
  or `-ConsumingProjectRoot`, else `not configured`. Editing inside that checkout is root rule 3's
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
