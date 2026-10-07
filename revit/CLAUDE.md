---
name: revit-host-onboarding
description: Onboarding for the Revit plugin — the 2025/2026/2027 range and why it compiles against 2025's API, the pure Core / Client / Addin-shim split, the `HPS-NN` rules this tree turns on, and the traps (CI never builds the shim, unexecuted Revit API calls, the maintainer-owned corpus). An index — the detail is in `revit/docs/` and `revit/README.md`. Read first for any change under `revit/`.
---

# Mantle Place for Revit — agent onboarding

Read the repo root [`CLAUDE.md`](../CLAUDE.md) first. This file is an index: each rule is stated
here in a line, and its detail lives in the document it links.

## Identity

- **Hosts:** Autodesk Revit **2025, 2026 and 2027**.
- **Compile target: Revit 2025's API** (`C:\Program Files\Autodesk\Revit 2025`), the oldest
  supported — not the newest installed. 2025/2026 run **.NET 8**, 2027 runs **.NET 10**, and one
  `net8.0-windows` assembly built against 2025's API loads in all three. The reverse fails: a
  `net8.0` project referencing Revit _2027_'s `RevitAPI.dll` errors with **`CS1705`**. So
  `RevitApiDir` pins the supported range, and raising it silently drops hosts. Revit 2024 is out of
  range (.NET Framework 4.8, where `System.Text.Json` is a package). The compile target forbids a **member** absent from 2025's API, by
  reflection or otherwise; a 2025 member whose shape differs in 2026 and later may be used when the
  element is asked what it accepts (`SubDivisionMaterial`). **Never branch on the version number.**
  Here *floor* means the oldest supported manifest version, never this.
- **SDK:** pinned in [`global.json`](./global.json) — the first thing that bites on a fresh machine.
- **Frame:** Revit is an **order-frame host** (`HPS-54`) — the survey point it applies, and the
  content placed against it, are in the **delivery CRS and its linear unit**, metric or foot, so the
  frame is a per-order fact. A file built in the AOI's metric UTM zone for the fixed-frame host (the
  shared drape bites) is not in this frame on a State Plane delivery, and is skipped with a stated
  reason (`HPS-53`), never reprojected. This host's block carries its own drape and vector layers in
  its own frame, and the planner places those first (`HPS-52`).
- **Role:** host #2, and the Host Plugin Standard's debugger. Where the four-layer shape does not
  fit .NET, that is a finding to file against the standard, not a thing to quietly work around.
- **Releases:** their own track, tagged `revit-<version>` with no `v`
  ([ADR 0001](../docs/adr/0001-per-host-release-tracks.md)); packaging needs `RevitAPI.dll` from
  Revit 2025, so it runs privately. The gate is the ribbon loading and one real import completing in
  2025, 2026 and 2027.

## The standard binds this folder

The Host Plugin Standard is **normative**; rules are cited by `HPS-NN` id in the code, and the ⛔
rules all guard one failure class: _the plugin appears to work_. Read the relevant section before
writing auth, the vault client, the bundle cache or anything touching the manifest. What each rule
means here, and which types carry it, is [`docs/standard-map.md`](docs/standard-map.md):
`HPS-02` triads · `HPS-31` one floor · `HPS-32` paths from `layout` · `HPS-33` verbatim, no
computed survey point · `HPS-36` `hosts.revit` only · `HPS-38`/`39` registration · `HPS-40`/`41`
the corpus at run time · `HPS-04`…`13` sign-in · `HPS-14`…`17` token stores · `HPS-18`…`25`, `48`
the vault · `HPS-55` the one background listing · `HPS-26`…`30`, `44` download and cache ·
`HPS-45` the one projection · `HPS-51` shared words · `HPS-52` own block first · `HPS-53` the frame
and datum refusals.

## Layout and the split that matters

```
src/MantlePlace.Revit.Core/    PURE. No Revit API, no I/O, no NuGet. net8.0.
src/MantlePlace.Revit.Client/  IMPURE, but not Revit. HTTP, cache, zip, secrets. net8.0.
src/MantlePlace.Revit.Addin/   IMPURE and Revit. Ribbon, transactions. net8.0-windows.
tests/MantlePlace.Revit.Core.Tests/   Headless, over Core AND Client. net8.0 + net10.0.
```

**Put logic in `Core`.** If you cannot assert it without launching Revit, it is in the wrong
assembly. The planner is the worked example: which topo path wins, what a pointer to a missing entry
does, whether shared coordinates may be set — decided in `BundleImportPlanner`, merely executed by
`RevitBundleImporter`.

**Put I/O in Client, not in the shim.** CI cannot build the shim, so anything there is covered by
review alone. `Client` references no Revit API, so a hosted runner builds and tests it — which is
what makes a test a real enforcer for ⛔`HPS-26`. Reach for `Addin` only for a `Document`, a
`Transaction` or the ribbon.

`Core` and `Client` stay `net8.0`; do not raise the target framework without a reason: Revit 2025 and 2026 run .NET 8, and the next .NET host extracts
its shared code **from this shipped code** (`HPS-43`). The suite multi-targets `net8.0;net10.0` and
CI runs both — the cheapest honest test of the three-versions-from-one-build bet.

## Commands

- **Build and test** → [`README.md` ▸ Build and test](./README.md#build-and-test), the one home. A
  `dotnet run` on the suite without `-f` cannot choose a framework and fails outright.
- **Quote paths with spaces** — `C:\Program Files\Autodesk\...`.
- **The Revit on this machine is a copy of the tree** (`HPS-50`): `tools/Check-RevitInstall.ps1`
  says whether it is `main`, a preview or stale; `tools/Deploy-MantlePlaceRevit.ps1` makes it `main`,
  and `-Launch` starts Revit 2027 for Hot Reload. **The deploy refuses while Revit is open** — the
  root `CLAUDE.md`'s post-merge deploy reports that as a leftover, never skips it silently. The loop,
  and a new build's first launch, a human's *Always Load* click no script can make →
  [`README.md` ▸ Loading it into Revit](./README.md#loading-it-into-revit).
- **The cross-host contract gate:** `python ../tools/manifest-conformance/check_manifest_conformance.py`.
- **Timing any commit in a real Revit** → [`tools/timing/README.md`](./tools/timing/README.md): a quiet
  Revit beside the install slot, one Revit at a time, a per-step table of where the seconds went.

## Naming

.NET conventions, spelled out in full: `MantlePlace.Revit.<Layer>` assemblies and namespaces,
PascalCase types and members, `_camelCase` private fields. Unreal's prefixes (`U`, `A`, `F`, `b`)
do **not** cross over.

**Ribbon face text is Title Case** — `Vault`, `Import Bundle`, `Probe Terrain` — because every
Autodesk tab beside ours does (`Toposolid`, `Site Component`), and a sentence-case verb phrase reads
as somebody's add-in. Name the thing, not the act (`Vault`, not `Open vault`). Every button sets a
one-line `ToolTip`, or Revit shows the `LongDescription` paragraph on hover. **Which words a shared
action takes is not this file's to decide** (`HPS-51`): sign-in and out, the vault, a local import,
the import window and its checklist, and the about surface take the standard's table, and a change
there moves the standard first. Title Case is Revit's; a host construct keeps its noun —
*toposolid*, never *toposurface*; shared words come from [`CONTEXT.md`](../CONTEXT.md).

## Things that will bite you

- **`UseWPF` changes the implicit-usings set.** The WindowsDesktop set omits `System.IO`, so the shim
  imports it explicitly; the symptom is a wall of `CS0103: The name 'Path' does not exist`.
- **The add-in shim is not built in CI, on purpose** (no Revit on a hosted runner). It is proven by a
  developer build plus a real import in Revit; if you change it, build it locally. Testable code put
  in the shim is hidden from CI, which is why `Client` exists.
- **Building the shim needs Revit 2025 specifically.** The project stops with one sentence when
  `RevitApiDir` has no `RevitAPI.dll`, in place of a forty-line `CS0246` storm.
- **The corpus is maintainer-owned.** Edit this host's own `verified-against.json` key freely, as
  text — a `json.load`/`json.dump` round-trip re-encodes the shared `$comment` block — and
  **propose** corpus cases by pull request, never add them unilaterally:
  a forked corpus is the drift the corpus exists to prevent.
- **The expiry skew is a constant with no parameter**, deliberately: the reference host takes it as an argument and its shim can pass `0`; here there is
  nowhere to put a zero.
  Do not add an override "for testability".
- **Revit API risk is real and not caught by the compiler.** Reflection tells you a member exists;
  only the compiler tells you its shape; only a real import tells you what it does. Which calls have
  run, in which versions, and which are compiled and unexecuted is
  [`docs/api-record.md`](docs/api-record.md) — the list the code means by "`revit/CLAUDE.md` lists
  them". `MANTLEPLACE_BUNDLE_ZIP` makes the import command skip its picker, for unattended runs
  (`LocalBundleSource`). Its entries, each with the rules it carries:
  - the ribbon, its imagery and `ToolTipImage` — the face, the theme repaint and the **355 px cap**
    are eyes-on-Revit checks ([ADR 0010](../docs/adr/0010-tooltip-vignettes-are-drawn-not-photographed.md));
  - the **staged import** — never `yield` inside an open transaction, never leave the
    `FailuresProcessing` hook attached across a slice;
  - ⛔ **the import window has a thread of its own, and Revit's window does not own it** — the
    record is the one home of why, and ⛔ **a step is announced only when measured at 30 s or more in any version**
    (`SlowStepNotice`);
  - the site location, context view and time zone; the attribution step, where ⛔ **the schema GUID
    is permanent** ([ADR 0011](../docs/adr/0011-revit-provenance-record-and-attribution-note-identity.md));
    the context buildings ([ADR 0012](../docs/adr/0012-context-buildings-come-from-the-site-model.md));
  - subdivisions with holes; a subdivision **refused at commit** after `CreateSubDivision` returned
    (`ImportFailurePolicy`); the hazard plan
    ([ADR 0014](../docs/adr/0014-revit-hazards-are-drawn-on-a-hazard-plan.md));
  - **a toposolid subdivision is a different element in 2025 than in 2026 and 2027** — what a retype
    costs, and where it runs; never "fix" a 2025-only observation into a universal comment;
  - the tree and shrub families and the harness that runs branch code in a Revit of its own; the
    Prepare notice, the Vault badge and the interrupted-Prepare rejoin; the published contours;
  - **the add-in is renderer-neutral** — it never writes a renderer's own storage.

## Where knowledge lives

- The bundle-manifest contract → the published JSON Schema series at
  `https://mantle.place/.well-known/schemas/bundle-manifest/`; this host's verified version lives in
  [`verified-against.json`](../tools/manifest-conformance/verified-against.json), never in prose.
- What each `HPS-NN` means here → [`docs/standard-map.md`](docs/standard-map.md); the rules
  themselves → [`docs/host-plugin-standard.md`](../docs/host-plugin-standard.md).
- What has run inside Revit, and the design records the code points to →
  [`docs/api-record.md`](docs/api-record.md).
- Signing in, tokens, refresh, sign-out →
  [`docs/platform-auth-contract.md`](../docs/platform-auth-contract.md); `TokenGrant.cs` and
  `PlatformError.cs` implement it, and both hosts share one stored credential.
- How this plugin behaves and how to build it → [`README.md`](./README.md).
- Whether this plugin supports a feature, and why not → the
  [host support matrix](../docs/host-support.md). A change that adds, narrows or drops a feature
  edits the **Revit** column in the same pull request, and only that column; a gap links its issue
  or ADR. Never state a per-host status anywhere else.
