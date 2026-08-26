# Mantle Place — DCC host plugins

Open-source plugins that bring **real-world terrain, imagery, buildings and site vectors** into the
tools designers actually work in.

| Host | Where | Status |
| --- | --- | --- |
| **Unreal Engine 5.8** | [`unreal/MantlePlace/`](unreal/MantlePlace/) | early access |
| **Autodesk Revit 2025 / 2026 / 2027** | [`revit/`](revit/) | early access |

Licensed under [Apache 2.0](LICENSE). The name is not covered — see [TRADEMARK.md](TRADEMARK.md).

<!-- media slot: hero GIF — draw an AOI on mantle.place, import the bundle, orbit the finished
     landscape in the Unreal editor. Keep it under a few MB; this repo has no LFS on purpose. -->

## What these plugins do

A **Mantle Place Bundle (MPB)** is a zip of pre-derived, host-ready geospatial artifacts — a
heightmap or terrain mesh, an imagery drape, building geometry, road centrelines, site boundaries,
tree points — plus a `Metadata/manifest.json` that describes them. The plugins read that manifest
and place the artifacts correctly in the host's own coordinate system. The format is specified in
public, in [`spec/`](spec/).

**The client is deliberately thin.** It applies numbers the platform already derived; it does not
re-derive them. The plugin never computes a survey point, a UTM zone, a landscape scale or a drape
extent of its own — it reads what the manifest publishes and applies it verbatim. That boundary is a
rule enforced at review, not an accident of the current design, and it is why this repository can be
open without anything important leaking out of it.

**Importing a local bundle needs no account.** No sign-in, no licence check, no server call. Point the
plugin at a bundle zip on disk and it imports. The vault browser — sign in, list what you own,
prepare and download — is a convenience on top of that, not a gate under it.

## The contract is published, not private

MPB is a **specified format**, and a release of it is three artifacts published together:

- **The schema** — a JSON Schema series served from
  `https://mantle.place/.well-known/schemas/bundle-manifest/`, the authority on what a manifest may
  contain. Two filename families: `v{N}.json` for the integer pre-history, and `{X.Y.Z}.json` for
  the MPB semver era — a consumer tells them apart by the JSON type of the manifest's own `version`
  field, a number meaning pre-history and a string meaning MPB.
- **The prose** — [`spec/`](spec/) in this repository: the format, the compatibility policy, the
  consolidated changelog, and what conformance means. It describes; the schema decides. Nothing in
  this repository restates a value the schema owns, and the version each host is verified against
  lives in
  [`tools/manifest-conformance/verified-against.json`](tools/manifest-conformance/verified-against.json)
  where CI checks it.
- **The [conformance corpus](tools/manifest-conformance/corpus/)** — language-neutral test vectors
  that **every** host runs against its own parser: accept and reject shapes, derived placement
  expectations, RFC known answers for PKCE and base64url, NIST FIPS 180-4 digests, the vault
  client's response vocabulary. A conforming reader passes it.

That means a second implementer is possible, and welcome. If you write a third-party consumer,
[`spec/`](spec/) is the document and the corpus is the test you can run against it — neither
requires reading a line of our plugin code.

## Get a real bundle in minutes

**There are no sample bundles in this repository, and there never will be** — no trimmed one, no small
one. Real geospatial data carries real licence obligations (much of it is ODbL), and a repository that
ships a bundle is redistributing that data. So the docs show you how to *make* one instead — and the
free tier makes that a matter of minutes, not a purchase decision.

**Areas of interest up to 2 km² are free.** $0, full paid-tier quality, an account but no payment
method. That is the intended way to evaluate these plugins: a real bundle over ground you know,
produced by the same pipeline as a paid order.

1. Create an account at [mantle.place](https://mantle.place) and draw an area of interest — keep it
   at or under 2 km² and the order is free.
2. Order a bundle for your host. The platform packages the artifacts and publishes a manifest.
3. Either sign in from inside the plugin and use the vault browser, or download the zip and use the
   local-import path:
   - **Unreal:** the Mantle Place panel → *Browse for vault zip*.
   - **Revit:** `Mantle Place ▸ Bundles ▸ Import bundle zip`, or set `MANTLEPLACE_BUNDLE_ZIP` and the
     picker is skipped entirely, so the import runs unattended from a script.

<!-- media slot: screenshot — the imported result in UE 5.8: the Landscape with painted weight
     layers and the imagery drape, viewport + outliner visible. -->

### What a downloaded bundle obliges you to do

A bundle is built from licensed geospatial sources, and some of those licences carry obligations —
most commonly an attribution requirement — that travel with the data into your project. Which
sources a given bundle used, which licences apply, and the exact attribution text they require are
stated by the platform, per bundle, not by this repository:
[mantle.place/licensing](https://mantle.place/licensing) describes the licences bundle data may
carry, and [mantle.place/attributions](https://mantle.place/attributions) is where the required
attribution statements live. Nothing here restates those values — the platform's pages are the
authority, and they are the ones kept current.

## Stream to compare, import to own

The Unreal plugin can show you the same bundle two ways in one viewport, and the pairing is the
clearest statement of what it is for.

The import path is the product: the bundle becomes **owned, engine-native assets on your disk** — a
single `ALandscape` with painted weight layers, the imagery drape, meshes, splines. It works offline,
needs no token, and survives the platform not being reachable, because after the download nothing is
streamed from anywhere.

Beside that, the plugin ships a streaming path built as a QA tool:
[`mantleplace_cesium_stream.py`](unreal/MantlePlace/Content/Python/mantleplace_cesium_stream.py)
starts a **local loopback tile server** that hosts the bundle's own Cesium-ready quantized-mesh
terrain and imagery, then spawns a `Cesium3DTileset` pointed at it — so
[Cesium for Unreal](https://cesium.com/platform/cesium-for-unreal/) (validated against 2.22.1)
streams your bundle next to the imported copy, with Cesium World Terrain alongside for
apples-to-apples comparison. Nothing streams from the Mantle Place platform; the server reads only
the local zip you already own.

The two paths do different jobs, and that is the point. Streaming answers *look at it now*; the
import answers *keep it — offline, forever, no token*. Cesium for Unreal is the natural companion
for the first job, and this plugin exists for the second.

<!-- media slot: GIF — the side-by-side: the streamed tileset and the imported Landscape of the
     same AOI in one viewport, camera panning between them. -->

## Repository layout

```
unreal/MantlePlace/            the Unreal plugin — MantlePlaceRuntime + MantlePlaceEditor
revit/                         the Revit plugin — pure Core, Client, Addin shim, headless tests
spec/                          the published MPB format spec — prose, policy, changelog
tools/manifest-conformance/    the contract gate + the shared conformance corpus
.github/workflows/             the three public CI gates, plus tracker hygiene
```

Start with each host's own docs: [`revit/README.md`](revit/README.md) for Revit; for Unreal, the
plugin source and [`unreal/MantlePlace/Docs/`](unreal/MantlePlace/Docs/).

## Building

**Revit** — no Revit install needed for the tests:

```bash
dotnet run --project revit/tests/MantlePlace.Revit.Core.Tests/MantlePlace.Revit.Core.Tests.csproj -f net8.0
dotnet run --project revit/tests/MantlePlace.Revit.Core.Tests/MantlePlace.Revit.Core.Tests.csproj -f net10.0
```

Building the add-in shim itself needs Revit 2025's API present — see
[`revit/README.md`](revit/README.md#build-and-test) for why the *oldest* supported version is the
compile target.

**Unreal** — drop `unreal/MantlePlace/` into a UE 5.8 project's `Plugins/` directory and let the
editor build it. Its only engine dependency is Epic's built-in Interchange.

**The contract gate** — Python 3.12, standard library only:

```bash
python -m unittest discover -s tools/manifest-conformance   # offline
python tools/manifest-conformance/check_manifest_conformance.py
```

## CI, and what it does not cover

Three workflows run on every pull request, on free hosted runners: `ci-manifest-conformance`,
`ci-revit-tests` and `ci-public-hygiene` — the last checks tracked files *and* the pull request's
title, body, branch name and commit messages for references that resolve only in a private
repository (see [CONTRIBUTING.md](CONTRIBUTING.md)). Together they are the objective merge bar.

**The Unreal compile is not among them.** It needs a licensed engine on Windows, and attaching a
self-hosted runner to a public repository would let a fork's pull request execute on the build
machine. So the engine build runs privately and at integration time. A pull request here can go green
and still break the Unreal compile; releases gate on that private build being green. This is a real,
accepted lag rather than a gap we would rather you not notice.

## Roadmap

Quarter-by-quarter, in [ROADMAP.md](ROADMAP.md). The headline: **World Partition large-AOI import**
— today an import produces a single `ALandscape`, and lifting that ceiling is the main course of the
plugin's path from early access to 1.0 on Fab.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) first — it is short, and it names the things that get a pull
request closed unread. In summary: DCO sign-off, no CLA, no binaries, no sample bundles, and auth and
the secret store are **report, don't patch** ([SECURITY.md](SECURITY.md)).

Maintenance is best-effort with a weekly triage batch and no SLA. That is stated here rather than
discovered later.

## Security

Report vulnerabilities privately through this repository's
[Security tab](../../security/advisories/new), or to **support@mantle.place**. Full policy:
[SECURITY.md](SECURITY.md).

## Why the history starts here

**This repository's history begins at its first commit, on purpose.** It is not a scrub, and nothing
was removed from a past that used to be here.

These plugins grew up inside a private monorepo that also vendors paid marketplace plugins — licensed
to us, and not ours to redistribute. Those files entered at that repository's **root** commit, which
is the fact that decides everything downstream: there is no edit at the tip that makes such a history
publishable, and a filtered import would rewrite every hash anyway. What it would produce is not
continuity but a costume — a commit graph you cannot check out, cannot build, and cannot bisect.
Given the choice between a fabricated lineage and an honest first commit, we took the first commit.
The timestamped record of independent creation stays in the private repositories, which is where it
does the job it exists for.

So there is no contributor graph here and no archaeology to read. The things that carry the weight
instead are the ones you can check yourself: public CI green on the merge bar, the conformance corpus
and the published schema it tests against, and documentation that states the gaps — the Unreal-compile
lag above is in this README because it is real, not because we ran out of places to hide it. The record
also reaches back further than this repository: the bundle-manifest schema series is published with an
append-only freeze ledger under `https://mantle.place/.well-known/schemas/bundle-manifest/`, so the
contract's iteration history is dated, externally served, and checkable without trusting a commit graph.

## How this code is written

Much of this code is written with AI assistance under human review, and the commit trailers say so.
We neither lead with that nor hide it. The questions worth asking about a patch are the same either
way: does the conformance suite still pass, does the thin-client boundary hold, and does the comment
explain *why* rather than restate the line beneath it. Those are the questions this project's review
asks of its own changes, and the ones it will ask of yours.

## Third-party notices

The fonts under `unreal/MantlePlace/Resources/Fonts/` are licensed under the SIL Open Font License;
their licence texts sit beside them. Autodesk, Revit, Epic Games, Unreal Engine and Cesium are marks
of their respective owners.
