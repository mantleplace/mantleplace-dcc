# The Mantle Place Bundle (MPB) spec

A **Mantle Place Bundle (MPB)** is the delivered format: a zip of pre-derived, host-ready
geospatial artifacts — terrain, imagery, surfaces, site geometry, vectors — plus a manifest that
describes them precisely enough to place them without guessing.

This directory is the public specification of that format. It exists so a second implementer can
write a consumer without reading our plugin code, and check that consumer against the same gates
our own plugins are held to.

## What decides, and what merely describes

Three artifacts make up an MPB release, and they are not equal in authority.

| | What it is | Authority |
| --- | --- | --- |
| **The schema series** | Published JSON Schema at `https://mantle.place/.well-known/schemas/bundle-manifest/` | **Normative.** The machine contract: every field, type, enum and constraint |
| **The conformance corpus** | [`tools/manifest-conformance/corpus/`](../tools/manifest-conformance/corpus/) | **Normative.** Language-neutral vectors; a conforming reader passes them |
| **This prose** | `spec/` | **Descriptive.** It explains the doctrine, the shape and the reasons |

**Prose describes; the schema and the gates decide.** Where this directory and the schema appear to
disagree, the schema is right and the prose is a bug — say so in an issue. Nothing here restates a
value the schema owns: no field lists, no enum members, no constraints, and — outside the
changelog, whose whole job is to name versions — no version numbers pinned into a sentence that
will quietly rot.

That division is the whole reason this spec can be short. A specification that re-types its own
schema has two contracts and no way to tell which one a producer honoured.

## Contents

- **[`format.md`](format.md)** — the format itself: what a bundle contains, the modality-folder
  vocabulary, the pointer doctrine, the manifest's structure, integrity, and how placement values
  are meant to be consumed.
- **[`compatibility.md`](compatibility.md)** — the versioning and compatibility policy: semver
  semantics, freeze-on-publish immutability, what a consumer must do with fields and versions it
  does not recognise.
- **[`conformance.md`](conformance.md)** — what "a conforming reader" means, and how to prove it.
- **[`changelog.md`](changelog.md)** — the consolidated version history, including the integer
  pre-history that predates this spec.

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
