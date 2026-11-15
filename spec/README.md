<!-- SPDX-License-Identifier: CC-BY-4.0 -->
<!-- SPDX-FileCopyrightText: Mantle Place LLC -->

# The Mantle Place Bundle (MPB) spec

A **Mantle Place Bundle (MPB)** is the delivered format: a zip of pre-derived, host-ready
geospatial artifacts — terrain, imagery, surfaces, site geometry, vectors — plus a manifest that
describes them precisely enough to place them without guessing.

This directory is the public specification of that format. It exists so a second implementer can
write a producer or a consumer without reading our plugin code, and check it against the same gates
our own plugins are held to.

MPB is a **profile**, not an encoding. It invents no way to store data: every file in a bundle is in a format somebody else specified, and MPB adds the tree, the names, the manifest, the rules for placing each file, and each file's licence and sources. In 3.x every file in the tree is promised for the life of the major ([`format.md`](format.md) §3).

## What decides, and what merely describes

Three artifacts make up an MPB release, and they are not equal in authority.

| | What it is | Authority |
| --- | --- | --- |
| **The schema series** | Published JSON Schema at `https://mantle.place/.well-known/schemas/bundle-manifest/` | **Normative.** The machine contract: every field, type, enum and constraint |
| **The conformance corpus** | [`tools/manifest-conformance/corpus/`](../tools/manifest-conformance/corpus/) | **Normative.** Language-neutral vectors; a conforming reader passes them |
| **The 3.x file table** | [`format.md`](format.md) §6 | **Normative** for the tree: every file's name, family, level and precision. The producer's registry is checked against it |
| **This prose** | `spec/` | **Descriptive.** It explains the doctrine, the shape and the reasons |

**Prose describes; the schema and the gates decide.** Where this directory and the schema appear to
disagree, the schema is right and the prose is a bug — say so in an issue. Nothing here restates a
value the schema owns: no field lists, no enum members, no constraints, and — outside the
changelog, whose whole job is to name versions — no version numbers pinned into a sentence that
will quietly rot.

That division is the whole reason this spec can be short. A specification that re-types its own
schema has two contracts and no way to tell which one a producer honoured.

## Contents

- **[`format.md`](format.md)** — the 3.x format: the tree of seven subject folders, the naming rules, the one promise and the admission rule, the two families of files and their units, the file table with every file's format, level and precision, the three levels, the manifest (one catalog, no per-host block), the zip, what left since 2.x and how to derive it, and where each value a 2.x plugin read from a host block lives now.
- **[`format-2.md`](format-2.md)** — the 2.x format, superseded and kept for the bundles delivered at 2.x, which are never repackaged: the pointer doctrine, the manifest's three registers and the `hosts.<hostId>` blocks, the canonical and courtesy formats, the 2.0 layout and its naming rules.
- **[`compatibility.md`](compatibility.md)** — the versioning and compatibility policy: how a reader tells 3.x from 2.x by where the manifest is, semver semantics (MAJOR/MINOR/PATCH), freeze-on-publish immutability, what a consumer must do with fields, values and versions it does not recognise, a higher major included, and the promise that nothing breaks within 3.x.
- **[`conformance.md`](conformance.md)** — what "a conforming reader" means, how to prove it, what claiming a corpus group obliges, and what a conforming 3.x bundle holds.
- **[`file-by-host.md`](file-by-host.md)** — the file-by-host table: sixty files of the 3.x tree opened in eight hosts before the text was frozen (the two real-time hosts only on the host-ready files; nine files of the tree were not opened by any host).
- **[`changelog.md`](changelog.md)** — the consolidated version history, including the integer pre-history that predates this spec.

## Licence

Which licence covers what:

| What | Licence |
| --- | --- |
| This prose (`spec/`) | [CC BY 4.0](../LICENSES/CC-BY-4.0.txt). Reuse it, with credit to Mantle Place |
| The schema series | [Apache 2.0](../LICENSES/Apache-2.0.txt) |
| The conformance corpus and the validator (`tools/manifest-conformance/`) | [Apache 2.0](../LICENSES/Apache-2.0.txt) |

The corpus holds **synthetic data only**: no real survey, parcel, imagery or customer data appears in
any vector. Each prose file carries an SPDX header; the corpus is marked Apache-2.0 in its
[README](../tools/manifest-conformance/corpus/README.md), because JSON vectors cannot carry a header.

You may say a producer or reader is "MPB 2.x-conformant" on the free, self-declared terms in
[`TRADEMARK.md`](../TRADEMARK.md#the-mpb-2x-conformant-statement).

## Which version am I reading against

Deliberately not written here. Two machine-readable files answer it, and both are checked by CI:

- **What exists**:
  [`frozen.lock.json`](https://mantle.place/.well-known/schemas/bundle-manifest/frozen.lock.json),
  served beside the schemas, names every published version and the sha256 of its exact bytes. Start
  there — **the schema namespace serves no directory listing**, so the bare path 404s by design and
  each schema is fetched at its own filename.
- **What our own plugins are verified against**:
  [`tools/manifest-conformance/verified-against.json`](../tools/manifest-conformance/verified-against.json),
  per host.

A published version never changes after publication — see [`compatibility.md`](compatibility.md) —
so pinning one is safe.

## Getting a bundle to read

There are no sample bundles in this repository and there never will be; the reasons and the
minutes-long path to a real one are in the [root README](../README.md#get-a-real-bundle-in-minutes).
A free-tier AOI produces a bundle from the same pipeline as a paid order, which is the only kind
worth testing a parser against.

## Scope

This spec covers the **portable** half of what a Mantle Place host plugin does: reading a bundle
that is already on disk. It is a one-way delivery format — the platform produces, consumers read.

It does **not** cover the Mantle Place vault's REST API. That is a service contract between our
plugins and our own platform, documented as SDK material rather than as interchange, and a
third-party consumer never needs it: importing a bundle from disk requires no account, no token and
no network call.

Nor does it cover what a host does with the data once placed. What a Landscape is, what a
toposurface is, how an engine wants its meshes — that belongs to each host's own documentation.
