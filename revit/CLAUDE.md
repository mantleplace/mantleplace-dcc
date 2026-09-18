---
name: revit-host-onboarding
description: Onboarding for the Revit plugin — the 2025/2026/2027 range and why it compiles against 2025's API, the pure Core / Client / Addin-shim split, the `HPS-NN` rules this tree turns on, and the traps (CI never builds the shim, unexecuted Revit API calls, the maintainer-owned corpus). Read first for any change under `revit/`.
---

# Mantle Place for Revit — agent onboarding

Read the repo root [`CLAUDE.md`](../CLAUDE.md) first. This folder is one host among several; the
root is one level up.

## Identity

- **Hosts:** Autodesk Revit **2025, 2026 and 2027**.
- **Compile target: Revit 2025's API** (`C:\Program Files\Autodesk\Revit 2025`), the oldest
  supported — not the newest installed. 2025/2026 run **.NET 8**, 2027 runs **.NET 10**, and one
  `net8.0-windows` assembly built against 2025's API loads in all three. The reverse fails at
  compile time: a `net8.0` project referencing Revit _2027_'s `RevitAPI.dll` errors with
  **`CS1705`**. So `RevitApiDir` is what pins the supported range, and raising it silently drops
  hosts. Revit 2024 is out of range — .NET Framework 4.8, where `System.Text.Json` is a package.
- **SDK:** pinned in [`global.json`](./global.json). This is the first thing that bites on a fresh
  machine.
- **Role:** host #2, and the Host Plugin Standard's debugger. Being maximally unlike Unreal is the
  point — where the four-layer shape does not fit .NET, that is a finding to file against the
  standard, not a thing to quietly work around.

## The standard binds this folder

The Host Plugin Standard is **normative**, in whatever language fits the host. Rules carry `HPS-NN`
ids and are cited by id throughout this tree. Before writing auth, the vault client, the bundle
cache or anything touching the manifest, read the relevant section — the ⛔ rules all guard the same
failure class: _the plugin appears to work_.

The ones this tree already turns on:

| Rule                  | What it means here                                                                                                                                            |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `HPS-02`              | every layer is a triad — impure shim / pure core / headless test. Protocol logic never goes in the shim.                                                      |
| `HPS-31`              | one supported manifest version, one home for the floor: `ManifestVersions.MinSupportedManifestVersion`.                                                       |
| `HPS-32`              | artifact paths come from `layout` (or the artifact block), never from folder convention.                                                                      |
| `HPS-33`              | manifest values are applied verbatim. This host does not compute a survey point.                                                                              |
| `HPS-36`              | read the `hosts.revit` subtree only. Never a sibling host's block, never the retired flat keys.                                                               |
| `HPS-38`/`39`         | `revit` is registered in `verified-against.json`, with its floor declared as path + regex.                                                                    |
| `HPS-40`/`41`         | the suite drives the shared corpus at run time and fails on an unknown expectation key.                                                                       |
| `HPS-04` … `13`       | PKCE `S256` in the system browser, loopback on the literal `127.0.0.1`, five-state machine driven from the corpus table.                                      |
| `HPS-14` … `17`       | refresh token via DPAPI, per-OS-user; access token memory-only; no store means memory-only auth, never a less-safe file.                                      |
| `HPS-18` … `25`, `48` | list → materialize → poll → **re-list** → presign → download; explicit token list, never a scope keyword; one error-body precedence for auth and vault alike. |
| `HPS-26` … `30`, `44` | write to `.part`, verify, rename; null sha is unknown not absent; eviction only on request.                                                                   |
| `HPS-45`              | `projection` IS claimed, for one thing only: the lon/lat `vector` layers behind roads, site boundaries and land cover. Nothing else here projects.            |
| `HPS-51`              | signing in and out, the vault, the local import, the import window and the about surface take the standard's words. The casing is this host's; the words are not. |

## Layout and the split that matters

```
src/MantlePlace.Revit.Core/    PURE. No Revit API, no I/O, no NuGet. net8.0.
src/MantlePlace.Revit.Client/  IMPURE, but not Revit. HTTP, cache, zip, secrets. net8.0.
src/MantlePlace.Revit.Addin/   IMPURE and Revit. Ribbon, transactions. net8.0-windows.
tests/MantlePlace.Revit.Core.Tests/   Headless, over Core AND Client. net8.0 + net10.0.
```

**Put logic in `Core`.** The test question is the design question: if you cannot assert it without
launching Revit, it is in the wrong assembly. The planner is the worked example — "which topo path
wins", "what happens when a pointer names a missing entry", "may we set shared coordinates" are all
decided in `BundleImportPlanner` and merely executed by `RevitBundleImporter`.

**Put I/O in `Client`, not in the shim.** Same question, different axis: CI cannot build the shim,
so anything living there is covered by review alone no matter how testable it is. `Client`
references no Revit API, so a hosted runner builds and runs it — which is what makes an automated
test a real enforcer for ⛔`HPS-26`. Reach for `Addin` only when the code needs a `Document`, a
`Transaction` or the ribbon.

`Core` and `Client` are `net8.0` rather than `net10.0` for two reasons: Revit 2025 and 2026 run on
.NET 8, and the next .NET host to land extracts its shared code **from this shipped code**
(`HPS-43`). Do not raise the floor without a reason.

The suite multi-targets `net8.0;net10.0` and CI runs both. Supporting three Revit versions from one
build is a forward-compatibility bet, and running the suite on both runtimes is the cheapest honest
test of it.

## Commands

**Build and test commands have one home: [`README.md` ▸ Build and test](./README.md#build-and-test)**
— the suite on both target frameworks, the full build including the shim, the `RevitApiDir` override,
and what to do when the SDK is a per-user install. They are not repeated here, because a second copy
is what drifts: the suite multi-targets `net8.0;net10.0`, so a `dotnet run` without `-f` cannot choose
a framework and fails outright.

**Quote paths with spaces** — `C:\Program Files\Autodesk\...`.

**The Revit on this machine is a copy of the tree, not the tree** (`HPS-50`). Before trusting
what it shows, `tools/Check-RevitInstall.ps1` says whether the installed add-in is `main`, a
preview, or stale; `tools/Deploy-MantlePlaceRevit.ps1` makes it `main` again, and with `-Launch`
starts Revit 2027 ready for Hot Reload of method bodies. The loop, including the one thing no
script can do (the first launch's *Always Load* click), is in
[`README.md` ▸ Loading it into Revit](./README.md#loading-it-into-revit).

The cross-host contract gate has no home in the README, so it is here (Python, offline for the corpus
half):

```bash
python ../tools/manifest-conformance/check_manifest_conformance.py
```

## Naming

.NET conventions inside this folder, spelled out in full (root `CLAUDE.md`: `mantleplace`, never
`mp`). Assemblies and namespaces are `MantlePlace.Revit.<Layer>`; types and members are PascalCase;
private fields `_camelCase`. The Unreal prefix tables (`U`, `A`, `F`, `b`) are Unreal's semantics and
do **not** cross over.

**Ribbon face text is Title Case** — `Vault`, `Import Bundle`, `Probe Terrain` — because that is what
every Autodesk tab beside ours uses (`Toposolid`, `Site Component`, `Property Line`), and a
sentence-case verb phrase is what makes the tab read as somebody's add-in. Name the thing rather than
the act wherever the button has one: `Vault`, not `Open vault`. A shared action takes the cross-host
word from [`CONTEXT.md`](../CONTEXT.md) — *vault*, *bundle*, *terrain* — and only a host construct
takes the host's own noun, *toposolid* and never *toposurface*. Every button also sets a one-line
`ToolTip`: without one Revit shows the `LongDescription` on hover, and that is a paragraph.

**Which words a shared action takes is not this file's to decide** (`HPS-51`): signing in and out,
the signed-in state and the wait either side of it, the vault, importing a bundle from disk and the
about surface are said the same way in every host, and the standard's table is where they are said.
Title Case is the part that is Revit's, and a host construct keeps its own noun — *toposolid*.
Changing one of those labels makes the other host wrong, so the standard moves first and both hosts
follow.

## Things that will bite you

- **`UseWPF` changes the implicit-usings set.** The WindowsDesktop set omits `System.IO`, so the
  shim imports it explicitly. Symptom is a wall of `CS0103: The name 'Path' does not exist`.
- **The add-in shim is not built in CI, on purpose** (no Revit on a hosted runner). It is proven by
  a developer build plus a real import in Revit. If you change it, build it locally — nothing else
  will catch a break. Corollary: putting testable code in the shim hides it from CI, which is why
  `Client` exists.
- **Building the shim needs Revit 2025 specifically**, not whichever Revit you happen to have. The
  project stops with one sentence when `RevitApiDir` has no `RevitAPI.dll`; the default forty-line
  `CS0246` storm it replaces was pure noise.
- **The corpus is maintainer-owned.** A change to this host edits its own `verified-against.json`
  key freely and **proposes** corpus cases by pull request rather than adding them unilaterally —
  a forked corpus is the drift the corpus exists to prevent. That extends to the file's _bytes_:
  a `json.load`/`json.dump` round-trip over `verified-against.json` silently re-encodes the shared
  `$comment` block. Edit your own key as text.
- **The expiry skew is a constant with no parameter.** That is deliberate — the reference host takes
  it as an argument and its shim can pass `0`. Do not add an override "for testability"; the point
  is that there is nowhere to put a zero.
- **Revit API risk is real and not caught by the compiler.** `RevitLinkType.CreateFromIFC`,
  `Toposolid.Create`, `ProjectLocation.SetProjectPosition`, and — added with the Forma-parity steps —
  `Toposolid.CreateSubDivision`, `DirectShape.SetShape` over curves,
  `GeometryCreationUtilities.CreateBlendGeometry`, and — added with the imagery drape —
  `AppearanceAssetEditScope`, the `UnifiedBitmap` schema and `ToposolidType.Duplicate` compile but
  have not yet been executed inside Revit. Compiling is worth more than nothing: it is what caught
  `AssetEditScope` not existing (it is `AppearanceAssetEditScope`), what surfaced
  `AssetPropertyDistance.GetUnitTypeId()`, which replaced a guess about texture units with a read,
  and what settled the scope of `Toposolid.SetSmoothedSurface` in one build — `CS0176` says it is
  **static**, so the setting is per document and the code that would have walked the subdivisions
  was never written. Reflection tells you a member exists; only the compiler tells you how it is
  shaped, and `GetMembers()` will happily list a static method as though it were an instance one.
  Treat their behaviour as unverified until a real import proves it — and note that `Toposolid`
  itself is Revit 2024+, so the 2025 floor is also the floor for the topo path. There is a way to
  drive them without a human: set `MANTLEPLACE_BUNDLE_ZIP` and the import command skips its file
  picker, so a journal or a test script can run it unattended (`LocalBundleSource`).
- **The ribbon is in that set too, and it has no unattended path at all.** The Account split button
  — `SplitButtonData`, `SplitButton.IsSynchronizedWithCurrentItem`, `RibbonItem.Visible` on a
  dropdown child, and `TaskDialog.AddCommandLink` — compiles, and the compiler is again worth more
  than nothing: it is what pins `SplitButton.AddPushButton` and the `RibbonItem` setters to their
  real shapes. What it cannot answer is whether the face repeats as the dropdown's first row, since
  `IsSynchronizedWithCurrentItem = false` is documented as "the first listed PushButton is shown"
  without saying whether that item is also listed. The row order in `BuildAccountPanel` is written
  to read correctly either way. A journal cannot settle it — `Jrn.RibbonEvent` executes a command
  and never reports what a button looked like — so this one is proven by opening Revit and looking,
  the same way the release gate is.
- **The ribbon's imagery joined that set.** `UIThemeManager.CurrentTheme`,
  `UIControlledApplication.ThemeChanged` with `ThemeChangedEventArgs.ThemeChangedType`, and
  `RibbonButton.Image`/`LargeImage` assigned on retained items compile and have not been executed
  inside Revit. The compiler earned its keep here too: it is what settled that `SplitButton` derives
  from `PulldownButton` and therefore has no `Image` of its own, so the face's picture is the first
  dropdown item's. What no compiler can answer is whether a `SplitButton` face actually draws that
  item's image, and whether a live theme change repaints a ribbon rather than needing a restart.
  Both are eyes-on-a-running-Revit, in both themes. What IS settled without Revit: the pure core
  picks the file name, the headless suite reads `Resources/` in both directions, and the built
  assembly's WPF resource table plus every pack URI in it can be checked from a script — see the
  pull request that added `RibbonImagery`.
- **`RibbonItem.ToolTipImage` is the newest member of that set.** It compiles — which is what pins
  down that it exists on `RibbonItem` and not only on `RibbonItemData`, so a vignette can be
  repainted after the ribbon is built and a theme change reaches it. What the compiler cannot say is
  whether Revit lays the picture out, whether it clips against a fifty-word `LongDescription`, or
  where the **355 px cap** actually bites: the API documents it in 2025, 2026 and 2027 alike and
  enforces it **silently**, so an over-large image is simply not there. That is why
  `Vignettes.MaxPixels` is asserted against the committed files' own PNG headers — no other detector
  is possible. Hovering the two Bundles buttons in a running Revit is the check, and it is the one
  manual step [ADR 0010](../docs/adr/0010-tooltip-vignettes-are-drawn-not-photographed.md) leaves
  open.

- **The staged import joined that set.** One step, or one chunk of trees, runs per
  `ExternalEvent` raise, and the handler posts the next raise at `DispatcherPriority.Background` so
  the window repaints and a Cancel click lands first. Whether Revit services a raise posted from
  inside its own handler promptly, whether the modeless window actually repaints between slices, and
  whether a Comments write on a `DirectShape` holds are compiled and unexecuted. What is settled
  headlessly is everything about *when*: `StagedImport` decides the slice order, where a cancel lands
  and what a failure costs, and `ImportChunking`/`TreeIdentity` decide the chunks and the resume.
  Never `yield` inside an open transaction — a chunked step commits, then yields — and never leave
  the session-wide `FailuresProcessing` hook attached across a slice boundary: between slices the
  curator is editing their own model (`RevitBundleImporter.InSlice`).
- **The context buildings joined that set.** The step converts the site model with
  `Application.OpenIFCDocument`, finds each building in the result by `BuiltInParameter.IFC_GUID`,
  clones its solids with `SolidUtils.Clone` and gives them to a Generic Model `DirectShape`. The
  converted document is closed in the slice that opened it, before the first chunk: a document held
  across slices is closed only when a step ends through `StagedImport`, and an import abandoned from
  the event handler does not. All of it compiles; none of it has run inside Revit. The part most
  likely to be wrong is the GlobalId: if Revit's import does not record it where the step looks, the
  step says so in one line and copies nothing. Which elements are buildings is not in that set — `SiteModelReader` reads it
  from the IFC's text, headlessly ([ADR 0011](../docs/adr/0011-context-buildings-come-from-the-site-model.md)).

## Where knowledge lives

- The bundle-manifest contract → the published JSON Schema series at
  `https://mantle.place/.well-known/schemas/bundle-manifest/` (`v{N}.json` for the integer
  pre-history, `{X.Y.Z}.json` for the MPB semver era). It is the authority; the
  version this host is verified against lives in
  [`verified-against.json`](../tools/manifest-conformance/verified-against.json), never in prose.
- Cross-host normative rules → the Host Plugin Standard, cited by `HPS-NN` id.
- Signing in, tokens, refresh, sign-out — what the platform must serve →
  [`docs/platform-auth-contract.md`](../docs/platform-auth-contract.md). Cross-host: `TokenGrant.cs`
  and `PlatformError.cs` implement it, and both hosts share one stored credential.
- What this plugin does and how to build it → [`README.md`](./README.md).
