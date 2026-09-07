---
name: unreal-host-onboarding
description: Onboarding for the Unreal plugin — UE 5.8 and Interchange, the two modules, how to drive the headless automation tests, the generated-name standard and where imported content lands, and the traps (CI never compiles this plugin, nothing the importer generates is saved, the identity is not the job id). Read first for any change under `unreal/`.
---

# Mantle Place for Unreal — agent onboarding

Read the repo root [`CLAUDE.md`](../CLAUDE.md) first. This folder is one host among several; the
root is one level up.

## Identity

- **Host:** Unreal Engine **5.8**, editor-side. The plugin folder plus its `.uplugin` is
  [`MantlePlace/`](MantlePlace/) — PascalCase within, and plugin discovery in a consuming project is
  a recursive scan of `Plugins/`.
- **Engine dependency:** Epic's built-in **Interchange**, and nothing else. Keep it that way; a new
  engine plugin dependency is a new thing a stranger's project has to have enabled.
- **Modules:** `MantlePlaceRuntime` (Runtime) and `MantlePlaceEditor` (Editor). The version lives in
  the `.uplugin` and nowhere else.
- **Role:** host #1, and the reference host. Where a rule was written with Unreal in mind, this is
  the tree that shows what it meant — which makes an expedient shortcut here more expensive than the
  same shortcut elsewhere.
- **Releases** are their own track, tagged `unreal-<version>` with no `v` — see
  [ADR 0001](../docs/adr/0001-per-host-release-tracks.md). There is no changelog file; the release
  body is the changelog.

## The standard binds this folder

The Host Plugin Standard is normative and is cited by `HPS-NN` id in the code. The ones this tree
turns on most visibly, and which a patch here is most likely to break:

| Rule     | What it means here                                                                                                                        |
| -------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| `HPS-32` | the paint-layer band legend is **data**. Which weight channel is which material is read from the manifest, never inferred from a filename. |
| `HPS-33` | manifest values are applied verbatim. A paint layer's name is the platform's material name exactly — never prettified, never prefixed.    |
| `HPS-46` | the conformance corpus's expectation keys are asserted. A case edited to expect something different turns the suite red.                   |

Root `CLAUDE.md`'s boundary section is the one to internalise: **this plugin applies pre-derived
values and never derives them.** A patch that computes a placement value locally is refused even when
the arithmetic is correct.

## Layout, and where logic goes

```
MantlePlace/Source/MantlePlaceRuntime/   auth, vault client, bundle cache, sha256. No editor, no UI.
MantlePlace/Source/MantlePlaceEditor/    the importer, the vault panel, the naming module.
MantlePlace/Source/*/Private/Tests/      headless automation tests over the pure logic.
MantlePlace/Content/                     shipped assets: the drape material, the auth Blueprint base.
MantlePlace/Content/Python/              the Cesium streaming helper. Spawns actors; imports no assets.
```

**Put the decision in a `*Logic` translation unit.** That is the pattern already here —
`MantlePlaceLandscapeWeightsLogic`, `MantlePlaceCoverageRasterLogic`, `MantlePlaceRoadSplinesLogic`,
`MantlePlaceTreePointsLogic`, `MantlePlaceVaultLogic`, `MantlePlaceAuthLogic`,
`MantlePlaceBundleCacheLogic` — and each one has a headless test beside it. The importer *executes*;
the Logic unit *decides*. The test question is the design question: if asserting it needs a running
editor and a real bundle, it is in the wrong translation unit.

This matters more here than it would elsewhere, because **CI never compiles this plugin** (below).
Logic in a pure unit is covered by a test you can run; logic in the importer is covered by review.

## Commands

There is no hosted build. The tests are Unreal automation tests, all named under a `MantlePlace.`
prefix, and the standard way to drive them unattended is:

```
UnrealEditor-Cmd.exe <YourProject>.uproject -ExecCmds="Automation RunTests MantlePlace." -unattended -nopause -nosplash -testexit="Automation Test Queue Empty" -log
```

`MantlePlace.Import.LiveFreeTier` is the exception — it wants a real bundle and real credentials, so
it is not part of an offline sweep.

```bash
# The cross-host contract gate (Python 3.12, standard library, offline for the corpus half).
python ../tools/manifest-conformance/check_manifest_conformance.py

# The generated-name drift gate. Text-only, no engine — run it before you push.
python ../tools/unreal-naming/check_generated_names.py
```

## Naming

Two different rules meet here, and conflating them is the mistake this section exists to prevent.

**Root `CLAUDE.md`'s "spelled out in full — `mantleplace`, never `mp`" governs the repository and the
code**: paths, folder names, module and class names, C++ symbols. It is not a rule about strings the
plugin writes into a user's project.

**Inside a user's project, `MP_` is the permitted marker on outliner-visible actor labels**, and only
there. The reason is in [ADR 0003](../docs/adr/0003-naming-authority-and-mp-prefix.md): an actor
label is read in a cramped outliner column and reappears unqualified in the Details panel, in logs
and in Blueprint references, and it is the only Mantle Place marker that survives a project whose
content root has been reconfigured.

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

An imported asset is *given* its name so it lands conforming, rather than being renamed afterwards;
renaming inside the import transaction is the hazardous path and exists only as a fallback for assets
a glTF brings with it.

### Where generated content lands

```
<ContentRoot>/<identity>/Imagery          the drape texture and its material instance
<ContentRoot>/<identity>/Mesh             the terrain static mesh
<ContentRoot>/<identity>/Buildings        the buildings static mesh
<ContentRoot>/<identity>/CoverageRasters  slope, water, canopy and their like, as textures
<ContentRoot>/<identity>/Landcover        one layer info per paint layer, plus the tree points table
```

- `<ContentRoot>` defaults to `/Game/MantlePlace` and is a **project** setting —
  `UMantlePlaceEditorSettings`, Project Settings → Plugins → Mantle Place, `config=Game`, checked
  into `DefaultGame.ini`. Deliberately not per-user: two developers with different roots in one
  project produce two copies of the same import that neither one's re-import will clean up. An
  unusable value falls back to the default rather than failing the import, because the importer
  creates and force-deletes a directory beneath it and a malformed root is not something to resolve
  creatively.
- `<identity>` identifies the *order*, not the build that produced it. See
  [ADR 0002](../docs/adr/0002-import-identity.md) — this is the part most likely to be got wrong, and
  getting it wrong duplicates a user's landscape silently.
- **The identity does not repeat in leaf asset names.** The folder already carries it.
- The **outliner** folder is `MantlePlace/<identity>`, fixed, and does *not* follow the content-root
  setting. Outliner folders have no project layout policy to collide with, and keeping it fixed keeps
  one predictable marker for support.

### Paint layers are not ours to name

A paint layer's name is what a landscape material's `LandscapeLayerBlend` nodes bind to, so it is the
platform's material name verbatim (`HPS-33`). Conforming those names to the table above would be
deriving a published value locally, which the boundary rule refuses. The legitimate half is already
done: the *asset* is `LI_<material>` while the layer's own name stays exactly as delivered.

### One place, not every call site

Every generated name and package path comes from `MantlePlaceImportNaming`. No `/Game/` literal, no
prefix literal and no subfolder literal at a call site — a convention with no single point of
definition rots at the first patch that does not know about it, and a naming regression compiles
cleanly.

Two things check it. `MantlePlace.Import.Naming` asserts the exact strings the module produces, and
`tools/unreal-naming/check_generated_names.py` refuses a `/Game/` literal, a bare prefix, an `MP_`
label, a subfolder path segment or an inline identity truncation anywhere else. The second exists
because the first only runs where an engine does — a line added at a call site would otherwise reach
`main` unchallenged. A line that genuinely needs one can carry `// naming-gate: allow <reason>`; the
reason is required.

### Re-import, and the two things that make it safe

Re-importing an order **replaces** its content: the destination directory is force-deleted and
rebuilt, because Interchange re-creates source-named assets. Two things stand in front of that
delete, and neither is optional.

**A provenance record**, written outside the content tree under the project's `Saved` directory. The
folder is named by the *truncated* identity, so two orders sharing eight characters name one folder;
the record holds the full identity, and a mismatch refuses instead of deleting. It lives outside the
content because it has to be readable at the moment the importer is deciding whether to delete that
content — loading an asset inside a folder about to be force-deleted is the hazard documented at the
delete itself. Its absence is also how content from 0.3.0 and earlier is recognised: that content is
refused, with its path, rather than adopted or silently replaced.

**An actor tag**, `mantleplace_import=<identity>`, carrying the full identity. This, not the label,
is what the stale-actor sweep matches. Labels are user-editable, and matching on them meant a user
who renamed an actor in the outliner broke their own next re-import and got a second landscape on
top of the first.

## Things that will bite you

- **CI never compiles this plugin.** It needs a licensed engine on Windows, and a self-hosted runner
  on a public repository would let a fork's pull request execute on the build machine. A green pull
  request here can still break the engine build — an accepted, published lag. Corollary: build it
  locally before you claim it works, and be suspicious of a type widening, which compiles at some
  call sites and silently rots others.
- **Nothing the importer generates is ever saved.** There is no `SavePackage` call in the plugin;
  every import task sets `bSave = false` and generated packages are only marked dirty. A doc comment
  or two says "saved" and is wrong. A user who closes without saving loses the import.
- **The identity is not the job id, and this is the mistake to expect.** `FMantlePlaceVaultManifest`
  offers `JobId` first and it reads like the obvious key. It changes on every rebuild. Anything
  keyed on it duplicates a user's content instead of replacing it, silently, and the symptom shows
  up one refresh later. `MantlePlaceImportNaming::ResolveIdentity` is the only place that decides.
- **The actor tag is load-bearing; the label is not.** Re-import matches the tag. Change the label
  format freely; change the tag and you have changed which actors a re-import destroys.
- **Re-import wipes before it writes.** Interchange re-creates source-named assets, so the importer
  force-deletes the destination directory first. Anything that widens what the destination path can
  be widens what that delete can reach — guard the inputs to it, not the delete.
- **[`.clang-format`](.clang-format) is for new code only.** The existing files predate it and are
  not clean against it. Nothing in CI checks formatting, and a reformat sweep is refused — see
  [CONTRIBUTING.md](../CONTRIBUTING.md).
- **No credentials in a `.uasset`, ever.** Property values are serialized, so a URL typed into a
  Blueprint's class defaults is in the committed file.

## Where knowledge lives

- The bundle-manifest contract → the published JSON Schema series at
  `https://mantle.place/.well-known/schemas/bundle-manifest/`. It is the authority; the version this
  host is verified against lives in
  [`verified-against.json`](../tools/manifest-conformance/verified-against.json), never in prose.
- Cross-host normative rules → the Host Plugin Standard, cited by `HPS-NN` id.
- Signing in, tokens, refresh, sign-out — what the platform must serve →
  [`docs/platform-auth-contract.md`](../docs/platform-auth-contract.md). Cross-host: Revit reads the
  same rejection codes from the same routes, and both hosts share one stored credential.
- Shared domain vocabulary → [`CONTEXT.md`](../CONTEXT.md).
- Why a decision was taken → [`docs/adr/`](../docs/adr/).
- What this plugin does and how to build it → [`README.md`](../README.md).
