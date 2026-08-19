# Mantle Place — DCC host plugins

Open-source plugins that bring **real-world terrain, imagery, buildings and site vectors** into the
tools designers actually work in.

| Host | Where | Status |
| --- | --- | --- |
| **Unreal Engine 5.8** | [`unreal/MantlePlace/`](unreal/MantlePlace/) | early access |
| **Autodesk Revit 2025 / 2026 / 2027** | [`revit/`](revit/) | early access |

Licensed under [Apache 2.0](LICENSE). The name is not covered — see [TRADEMARK.md](TRADEMARK.md).

## What these plugins do

A Mantle Place **bundle** is a zip of pre-derived, host-ready geospatial artifacts — a heightmap or
terrain mesh, an imagery drape, building geometry, road centrelines, site boundaries, tree points —
plus a `Metadata/manifest.json` that describes them. The plugins read that manifest and place the
artifacts correctly in the host's own coordinate system.

**The client is deliberately thin.** It applies numbers the platform already derived; it does not
re-derive them. The plugin never computes a survey point, a UTM zone, a landscape scale or a drape
extent of its own — it reads what the manifest publishes and applies it verbatim. That boundary is a
rule enforced at review, not an accident of the current design, and it is why this repository can be
open without anything important leaking out of it.

**Importing a local bundle needs no account.** No sign-in, no licence check, no server call. Point the
plugin at a bundle zip on disk and it imports. The vault browser — sign in, list what you own,
prepare and download — is a convenience on top of that, not a gate under it.

## The contract is published, not private

The bundle manifest is a **published JSON Schema**, served from
`https://mantle.place/.well-known/schemas/bundle-manifest/v{N}.json`. The schema series is the
authority on what a manifest may contain; nothing in this repository restates it, and the version each
host is verified against lives in
[`tools/manifest-conformance/verified-against.json`](tools/manifest-conformance/verified-against.json)
where CI checks it.

That means a second implementer is possible, and welcome. The
[shared conformance corpus](tools/manifest-conformance/corpus/) is language-neutral test vectors that
**every** host runs against its own parser — accept and reject shapes, derived placement expectations,
RFC known answers for PKCE and base64url, NIST FIPS 180-4 digests, the vault client's response
vocabulary. If you write a third-party consumer, that corpus is the spec you can test against.

## Getting a bundle to try

**There are no sample bundles in this repository, and there never will be** — no trimmed one, no small
one. Real geospatial data carries real licence obligations (much of it is ODbL), and a repository that
ships a bundle is redistributing that data. So the docs show you how to *make* one instead.

1. Create an account at [mantle.place](https://mantle.place) and define an area of interest.
2. Order a bundle for your host. The platform packages the artifacts and publishes a manifest.
3. Either sign in from inside the plugin and use the vault browser, or download the zip and use the
   local-import path:
   - **Unreal:** the Mantle Place panel → *Browse for vault zip*.
   - **Revit:** `Mantle Place ▸ Bundles ▸ Import bundle zip`, or set `MANTLEPLACE_BUNDLE_ZIP` and the
     picker is skipped entirely, so the import runs unattended from a script.

## Repository layout

```
unreal/MantlePlace/            the Unreal plugin — MantlePlaceRuntime + MantlePlaceEditor
revit/                         the Revit plugin — pure Core, Client, Addin shim, headless tests
tools/manifest-conformance/    the contract gate + the shared conformance corpus
.github/workflows/             the two public CI gates
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

Two workflows run on every pull request, on free hosted runners: `ci-manifest-conformance` and
`ci-revit-tests`. Together they are the objective merge bar.

**The Unreal compile is not among them.** It needs a licensed engine on Windows, and attaching a
self-hosted runner to a public repository would let a fork's pull request execute on the build
machine. So the engine build runs privately and at integration time. A pull request here can go green
and still break the Unreal compile; releases gate on that private build being green. This is a real,
accepted lag rather than a gap we would rather you not notice.

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

## Third-party notices

The fonts under `unreal/MantlePlace/Resources/Fonts/` are licensed under the SIL Open Font License;
their licence texts sit beside them. Autodesk, Revit, Epic Games, Unreal Engine and Cesium are marks
of their respective owners.
