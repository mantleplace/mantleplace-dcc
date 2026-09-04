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
| `HPS-45`              | `projection` IS claimed, for one thing only: the lon/lat `vector` layers behind roads and site boundaries. Nothing else here projects.                        |

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

- **Quote paths with spaces** — `C:\Program Files\Autodesk\...`.
- If the SDK is a per-user install, put `~/.dotnet` on `PATH` for the session first.

```bash
# Pure core + conformance suite. No Revit required; this is what CI runs.
dotnet run --project tests/MantlePlace.Revit.Core.Tests/MantlePlace.Revit.Core.Tests.csproj

# Everything including the shim. Requires a local Revit install.
dotnet build MantlePlace.Revit.slnx

# The cross-host contract gate (Python, offline for the corpus half).
python ../tools/manifest-conformance/check_manifest_conformance.py
```

## Naming

.NET conventions inside this folder, spelled out in full (root `CLAUDE.md`: `mantleplace`, never
`mp`). Assemblies and namespaces are `MantlePlace.Revit.<Layer>`; types and members are PascalCase;
private fields `_camelCase`. The Unreal prefix tables (`U`, `A`, `F`, `b`) are Unreal's semantics and
do **not** cross over.

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

## Where knowledge lives

- The bundle-manifest contract → the published JSON Schema series at
  `https://mantle.place/.well-known/schemas/bundle-manifest/` (`v{N}.json` for the integer
  pre-history, `{X.Y.Z}.json` for the MPB semver era). It is the authority; the
  version this host is verified against lives in
  [`verified-against.json`](../tools/manifest-conformance/verified-against.json), never in prose.
- Cross-host normative rules → the Host Plugin Standard, cited by `HPS-NN` id.
- What this plugin does and how to build it → [`README.md`](./README.md).
