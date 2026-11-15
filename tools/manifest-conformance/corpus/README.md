<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- SPDX-FileCopyrightText: Mantle Place LLC -->

# The shared conformance corpus

Language-neutral test vectors that **every** Mantle Place host plugin runs against its own parser.

**This corpus is normative.** It ships as one third of an MPB spec release — the schema, the prose
in [`spec/`](../../../spec/), and these vectors, published and versioned together — and it is the
part that decides: *a conforming reader passes the corpus*. The prose describes; the schema and this
corpus settle disagreements. See [`spec/conformance.md`](../../../spec/conformance.md) for what
claiming a group obliges you to, and which groups a third-party bundle reader needs at all.

The JSON vectors beside this file are Apache-2.0 and contain synthetic data only; an SPDX header covers
only its own file, so this file's header does not say so for them.

This file is procedural: how to consume the vectors. What a reader owes the manifest is
[`spec/format.md`](../../../spec/format.md); the additional obligations a first-party Mantle Place
plugin carries are the Host Plugin Standard's (`HPS-40`, `HPS-41`).

## Why it exists

The Unreal plugin had all of this already, as inline C++ string literals inside
`MantlePlaceImportManifestTest.cpp` and friends. That is a perfectly good test suite and a
completely unusable specification: a .NET or Python host could read the vectors only by reading
C++, and would inevitably re-derive them slightly differently. The bugs that follow — a `null`
sha256 read as "corrupt" instead of "unknown", a `+` left unescaped in a PKCE verifier, a UTM false
northing dropped in the southern hemisphere — are all silent. They produce a plugin that works on
the developer's bundle and misplaces a customer's site by kilometres.

Lifting the vectors here makes them one artifact with one owner, and makes "the second host agrees
with the first" a thing a test can assert instead of a thing a reviewer hopes.

**The Unreal suite now reads these files at run time rather than duplicating them** — six automation
tests across both plugin modules. Editing a case here turns those tests red, which is the only proof
that the corpus and the reference host have not quietly diverged. The `digest` group is the sharpest
demonstration: one edit to `sha256-vectors.json` fails two independent SHA-256 implementations
inside the same host.

## Using it from a host suite

Read `index.json`, iterate `cases`, and dispatch on `group` + `expect`:

| `expect` | What the host asserts                                                                                                                                                                              |
| -------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `accept` | the parser accepts `file` and produces every value in `expectations`                                                                                                                               |
| `reject` | the parser **refuses** `file`; if `errorContains` is present the message contains it. A reject case may still carry `expectations` — values the parser must have read _before_ refusing (`HPS-37`) |
| `vector` | `file` is a known-answer table, not a document to parse — drive it row by row                                                                                                                      |

A case with `appliesTo` asserts one host's manifest block; every other host skips it (`HPS-41`).

`appliesTo` names a host registered in `verified-against.json`, or a planned one whose plugin has not
landed (`_PLANNED_HOSTS` in `check_manifest_conformance.py`). No registered host covers a planned
host's case, so the baseline records it as `[]` until the host registers; registering removes the name
from that set and updates the baseline. No host is planned today. The `hosts.autocad` cases
(`manifest.autocad*`) are the `autocad` host's, which claims the `manifest` group; they assert only
values the block states, so a plain AutoCAD reader and a Civil 3D reader both run them.

Numeric tolerances are stated in the case (`landscapeSpawnToleranceCm`, `toleranceMetres`); do not
invent tighter ones, and do not compare floats exactly.

A host does not have to consume every group on day one — a manifest-first build order starts
with `manifest` and adds `vault`/`auth` as those layers land. It does have to consume
every case _in the groups it claims_, and `HPS-41` is what says so.

## Adding a case

The corpus is **maintainer-owned**. A host that wants a new case proposes it here by pull request
rather than forking a private copy — a forked corpus is the same drift the corpus exists to
prevent.

Every case needs a `reason`. A fixture nobody can explain is deleted the first time it is
inconvenient, usually by the person it would have saved.

The mechanical checks — required fields, unique ids, files present and parseable, versions
agreeing, no orphans — run offline in CI. `check_manifest_conformance.py` owns that list; it is not
restated here.

In practice a case is one pull request with these edits:

1. The fixture, under its group's folder: JSON, two-space indent. Compare a re-dumped file with
   git, not byte-for-byte: a Windows checkout holds CRLF where git stores LF.
2. Its entry in `index.json`, with its `reason`.
3. Each claiming host's assertions. Revit refuses an expectation key it does not consume, so a new
   key goes into `ConsumedExpectationKeys` in
   [`ManifestConformanceTests.cs`](../../../shared/tests/MantlePlace.Core.Tests/ManifestConformanceTests.cs)
   beside the assertion that reads it (`HPS-46`).
4. When the checker reports changed coverage, `check_manifest_conformance.py --update-baseline`, with
   the `coverage-baseline.json` diff committed for review.

A coverage **drop** is a lost assertion. Retiring a case is a deliberate edit with its reason in the
pull request body, never a baseline update made to turn the gate green.

## The bundle group (`bundle/`)

The other groups hold a reader's parser; `bundle` holds a **validator**: a tool that judges a
whole bundle conformant or not, with reasons. No host plugin claims it.

A `bundle` case is a JSON description of a synthetic bundle, never a bundle: `files` maps each
archive path to its content, a string written as UTF-8 or a JSON value written as JSON, so the
corpus stays text and holds no sample data. One JSON value is a description and not file content:
`{"$geopackage": {"tables": ["RoadSplines"]}}` is built as a GeoPackage that holds one empty
feature table per name, for the cases that count a file's tables (`bundle.v2GpkgSoleTable`). A validator builds each case as a folder, a zip and
a zip inside one wrapper folder, and must reach the same result on all three:

| Field | What the validator must report |
| --- | --- |
| `expect` | `accept`: the bundle conforms; `reject`: it does not |
| `expectations.errors` | exactly these finding codes at error severity, and no others |
| `expectations.warnings` | exactly these at warning severity; absent means none |
| `expectations.hashesVerified` | the number of files whose sha256 it verified |

The codes are those [`mantleplace-mpb`](../../mantleplace-mpb/README.md) reports, and
`mpb corpus` is the runner. A manifest in a case declares its own version and is held to that
published schema, so a case is fetched against the network the way the contract gate is.

Two case fields say how a case is built and fetched, not what it must report:

- `stagedSchema: true` marks a case whose manifest declares a **staged** version: served for
  readers, not yet in the freeze ledger. The runner holds it to the served schema, as
  `mpb validate --staged` does, and reports it at info (`schema.staged`). The 3.0 cases that
  declare 3.0.0 carry it while 3.0.0 is staged; once it is frozen the field changes nothing and is
  removed, as it was from the 2.0 cases when 2.0.0 was frozen.
- A case whose paths differ only by case (`bundle.v2NameCaseCollision`) is built as the zip and
  the wrapped zip only. No folder on a case-insensitive file system can hold it, which is the
  reason for the rule it tests.

The 2.0 cases hold every 2.0 bundle to the folder allowlist and the naming rules
([`format.md`](../../../spec/format.md) §10.2, §10.3): `bundle.v2Full` is the default download
whole, each rule a validator can check has a case that breaks it, and the allowlist's two
warnings, an unlisted folder and a stray root file, are accept cases.

The 3.0 cases (`bundle.v3*`) hold a 3.x bundle to its tree, its names, its one level and its
catalog ([`format.md`](../../../spec/format.md) §1, §2, §8, §9.5; the requirements are
[`conformance.md`](../../../spec/conformance.md) §7): `bundle.v3Full` is a Max download whole,
`bundle.v3FullMed` and `bundle.v3FullMin` the same bundle at the other two levels,
`bundle.v3MedWithoutRecords` a Med download of an AOI with no records, `bundle.v3RecordsNotHeld` a Max
download that lists the twenty Records files it does not hold with each reason, `bundle.majorAhead` a
manifest of a major no schema is published for, and each rejection has a case that breaks one rule. `bundle.v3MinorFileUnknown` declares 3.1.0, a minor that may add a file,
so a file the 3.0 table lacks is a warning there and an error in `bundle.v3FileUnknown`. Every case
that declares 3.0.0 is held to the served 3.0.0 schema (`stagedSchema`), and is valid against it
except where the rule it breaks is one the schema states too: a catalog key outside the 3.0 file
table, or a level stated twice or not at all. Those cases expect the schema's refusal
(`schema.invalid`) beside their own code. No schema is served for 3.1.0, so
`bundle.v3MinorFileUnknown` is held to the format text's rules, and the runner reports the
unpublished schema at info (`schema.pending`).

## The reader group (`reader/`)

The `reader` group is the answer sheet for a **reader of either major**: for one synthetic bundle, what
a correct reader finds in it. It is data, so a reader in any language runs the same cases and reaches
the same result, and no two readers read 3.0 a little differently. No host claims it yet; a host
claims it when its reader runs every case (`HPS-41`), and `mpb` reads 2.x only.

A `reader` case is a pair. The case file names a bundle and holds the result:

| Member | What it holds |
| --- | --- |
| `bundle` | the path, under the corpus, of the `bundle` case file that describes the bundle (`bundle/v3-full.json`). The bundle is built from its `files` as the `bundle` group says: a folder, a zip and a zip in one wrapper folder, with the same result from all three. A bundle a validator refuses may be read by a reader (`reader.v3CatalogFileMissing`) |
| `result` | exactly the members below, in this order |

| `result` member | What the reader must report |
| --- | --- |
| `outcome` | `read`, or `refused` when it stops at the manifest's version. The case's `expect` is `accept` for `read` and `reject` for `refused` |
| `major`, `version` | the major and the semver string it read from the manifest |
| `manifestPath` | where it found the manifest: `Manifest.json` for a 3.x or later bundle, `Metadata/manifest.json` before ([`compatibility.md`](../../../spec/compatibility.md) §1) |
| `level` | the download's level, `Max`, `Med` or `Min`, as the manifest states it once; `null` where a bundle states none (every 2.x bundle) or the reader refused |
| `refusal` | `null` for a read; `major-ahead` for a major above 3, refused before any file is extracted ([`format.md`](../../../spec/format.md) §14). The case's `errorContains` is the version, which the message names |
| `files` | the files it finds, one object each, sorted by `path`: a catalog name that the bundle holds |
| `skipped` | the files it names and does not read, one object each, sorted by `path` |

A **found file** is `{"path", "family", "frame", "count"}`. `path` is the catalog's key. `family` is
`georeferenced` or `host-ready`, or `null` for a file in neither (`Readme.md`, `Manifest.json`, `Terrain.mtl`);
in 3.x it is the extension's ([`format.md`](../../../spec/format.md) §4), in 2.x the file's own frame
statement (`absolute` is georeferenced, `offset` host-ready, none is `null`). `frame` is the frame its
coordinates are in and always pairs with the family: `order` (absolute, in the order's CRS) for
`georeferenced`, `site` (offsets from the site origin) for `host-ready`, `null` for `null`. `count` is
the count the catalog **states** for the file, rows, features, triangles or pixels, and `null` when it
states none: a reader reports what the manifest says and never counts a file for itself.

A **skipped file** is `{"path", "why", "reason"}`, with `why` one of two:

- `file-missing`: the catalog names it and the download holds no such file. It is reported and never
  fatal; `reason` is `null`. The reader still reads every file it does find.
- `not-held`: the manifest lists it among the promised files the download does not hold
  ([`format.md`](../../../spec/format.md) §9.6). `reason` is the reason the manifest states, **verbatim**.
  A reader shows it and never branches on it: the reasons grow, and a case holds one no reader's
  table lists (`withheld`).

A file in the bundle that the catalog does not name is not found and is not skipped. A path the
catalog names is matched as written; matching regardless of case is the reader's own rule and no case
depends on it.

**Running the cases.** For each case of group `reader` in `index.json`: read the case file, build the
bundle it names, run the reader on it, and compare. `files` and `skipped` are compared as sets keyed by
`path` (the order is for the human who reads them), every member of an entry exactly, and a count
as an integer. A `reject` case asserts that the reader refuses, that the message contains
`errorContains`, and that the members it must have read first (`version`, `major`, `manifestPath`) are
the result's. A reader claims the group by running every case and failing on one it did not run
([`conformance.md`](../../../spec/conformance.md) §3).

`check_manifest_conformance.py` holds every case file to this shape, and to its bundle
(`reader_cases.py`): the members, their types and the closed vocabularies; the lists sorted, with each
path once; a `reject` case for a refusal and an `accept` case for a read; and the whole result
**re-derived from the bundle's manifest**, so the answer cannot drift from the fixture it answers for.
The bundle's manifest states them in the served 3.0.0 schema's members: the level in `level`, the
catalog in `catalog`, a count in an entry's `count` as `{"kind", "value"}`, whose `value` is the
result's count, and the files it does not hold in `not_held`, a list of `{"path", "reason"}`.

The cases: `reader.v3FullMax`, `reader.v3FullMed` and `reader.v3FullMin` are the whole 3.0 file table
at each level; `reader.v3RecordsNotHeld` and `reader.v3MedWithoutRecords` are a bundle missing optional
subjects, with and without a not-held list; `reader.v3MinorFileUnknown` is a 3.x minor's added file,
found by its catalog name; `reader.v3CatalogFileMissing` is a catalog name with no file;
`reader.v2Full` is a manifest at the 2.x location, read by the same runner; `reader.majorAhead` is a
major above 3, refused. Every `bundle` case that conforms and declares 3.x has a case.

## The Platform API groups (`platform/`, `client/`)

These two groups hold **exchanges** with the Platform API, not documents a parser reads: one request
a host sends and the answer the platform gives. `platform` holds the Quote and the order refusals;
`client` holds the answer any call can get, `client_update_required`. A case file is:

| Field | What it holds |
| --- | --- |
| `operationId` | the operation in the served spec (`https://mantle.place/api/v1/openapi.json`) |
| `request` | `method`, `path`, and the `query`, `headers` and JSON `body` the host sends; `omits` names a required parameter the case leaves out on purpose |
| `response` | the `status` and JSON `body` the platform answers with |

A host replays the request through its client against a stub that answers with the response, and
asserts what it sent and what it read. `expect` is `accept` for a success and `reject` for a
refusal; `expectations` name the values read (`status`, `code`, `handoffUrl`, and so on).

`check_manifest_conformance.py` holds every case to the spec's own schemas: the operation, its
parameters, the request body, the status and the response body (`platform_cases.py`). A pull
request reads the vendored copy beside the generated .NET client, which `ci-platform-api` keeps
equal to the served one; the scheduled live run reads the served copy too. The spec the cases were
checked against is recorded twice, as its `info.version` and the sha256 of its canonical text: in
`coverage-baseline.json` (`platformApi`) and in each claiming host's `verifiedAgainstPlatformApi`.
When the spec moves, the gate fails until the cases are re-checked and both records raised.

## The self-test corpus (`self-test/`)

`self-test/` holds deliberately broken index and case fixtures that every host's corpus **reader**
must reject (`HPS-46`) — an expectations key no host may consume, a known key declared with the
wrong JSON type, a missing case file, an orphan, undeclared malformed JSON, a duplicate id, and two
index-level breakages in `broken-index-json/` and `broken-index-schema/`. A fixture that passes is
the failure. It proves the reader, where the corpus proper proves the parser; host suites never mix
the two, and `check_manifest_conformance.py` verifies the set is _well-formed-broken_ — each fixture
wrong in exactly its declared way. Expectation keys prefixed `selfTest` are a reserved namespace no
host may ever consume.

## Provenance

| Group        | Lifted from                                                                                                       |
| ------------ | ----------------------------------------------------------------------------------------------------------------- |
| `manifest`   | `MantlePlaceImportManifestTest.cpp` (accept shape from the platform's published schema and its reference fixture) |
| `vault`      | `MantlePlaceVaultLogicTest.cpp`                                                                                   |
| `auth`       | `MantlePlaceAuthLogicTest.cpp` (RFC 7636 Appendix B, RFC 4648 §5)                                                 |
| `cache`      | `MantlePlaceBundleCacheLogicTest.cpp`                                                                             |
| `digest`     | `MantlePlaceSha256Test.cpp` + the cache suite (NIST FIPS 180-4 known answers)                                     |
| `projection` | `MantlePlaceRoadSplinesLogicTest.cpp` (`mesh.origin` of the reference fixture)                                    |
| `bundle`     | written for the bundle validator; the manifests start from the platform's minimal readiness-only manifest          |
| `reader` | written for the readers of both majors; the bundles are the `bundle` group's, the results are derived from their manifests |
| `platform`, `client` | written against the served Platform API spec; synthetic values throughout                          |

"Lifted from" means the _assertions_ came from those files, not that every fixture is byte-identical
to its C++ literal. Where a vector was renamed or padded for readability (placeholder sha values,
clearer decoy paths) the behaviour it exercises is unchanged; `manifest/full.json` is the one
that is value-exact — every number is carried digit-for-digit from the reference fixture, because
its derived expectations depend on the exact values. (Its keys were mapped to the MPB 1.0.0
dialect along with the rest of the corpus, and its `job_id` label is corpus-local; the numbers are
the preserved thing.)

Note the `projection` and `manifest` groups describe **different AOIs** — the projection pair comes
from `mesh.origin`, the manifest fixture from `unreal.georeference.origin`. They are not expected to
agree, and the case file says so.

## Version

Fixtures are written against the pinned manifest version unless a case declares a later minor of
its own (below), and the readers' floor is that same version. Everything below it is in the reject
set: clean break, one supported version (`HPS-31`). The floor and the pin are deliberately split —
`index.json`'s `manifestVersion` is the pin, and `verified-against.json` records each host's own.

**Two version families.** A `manifestVersion` is an integer for the pre-history (`19`) and a semver
string for the MPB era (`"1.0.0"`); the JSON type is what tells them apart, and the whole integer
era sorts below the whole semver era. The pre-floor rejects therefore span both: the ladder runs
`v6, v7, v13, v14, v16, v17, v18, v19`, closing the integer era entirely. Those fixtures are
deliberately left in the **old dialect** — `jobId`, a top-level `unreal` block, no `hosts` — because
being written in a dialect this reader no longer speaks is the thing they test. Key-mapping them
forward would quietly destroy them.

Case ids and filenames are **version-agnostic** on purpose (`manifest.full`, not
`manifest.v19.full`). Only the explicit version-gate rejects carry a number. A version bump is then
three moves — new accept shape, previous version to the reject set, `manifestVersion` repinned —
rather than a rename sweep across every case a host suite references.

**An additive minor adds a case; it does not re-stamp the corpus.** Those three moves are for a
release that moves the floor. A minor leaves the floor where it is, and a same-major reader must
read both shapes, so the existing fixtures stay the documents they are and the new shape arrives as
a case carrying its own `manifestVersion` — `manifest.locationBlockIgnored` is the first,
`manifest.foliageVocabularyIgnored` the second, `manifest.hostFramesIgnored` the third,
`manifest.hazardLayersIgnored` the fourth, `manifest.basemapFilesIgnored` the fifth,
`manifest.siteBriefIgnored` the sixth, `manifest.partialDeliveryIgnored` the seventh,
`manifest.verticalDatumsIgnored` the eighth, `manifest.fidelityLevelsIgnored` the ninth,
`manifest.bundlePartsIgnored` the tenth, `manifest.recordPointersIgnored` the eleventh,
`manifest.recordsFolderIgnored` the twelfth, `manifest.sourcePointersIgnored` the thirteenth and
`manifest.fileFramesIgnored` the fourteenth, with three siblings that pin one reader obligation each
(`manifest.fileEntryNoRole`, `manifest.fileEntryUnknownRole` and `manifest.fileEntryVerticalCrsApart`).
The fifteenth, 1.15.0, restated a pointer rather than adding a member, so its cases pin the Revit host's
reading of it: `manifest.revitVectorCarrier` and `manifest.reject.revitCarrierHashMissing`.
The 2.1.0 and 2.2.0 minors are the first manifest cases stamped 2.x: `manifest.imageryReflectanceIgnored` and
`manifest.imageryReflectanceNull` (2.1.0), `manifest.heightmapVerticalTierIgnored` (2.2.0) and its Unreal sibling
`manifest.unrealHeightmapVerticalTier`, which pins the range the Unreal plugin reads beside the tier members.
Stamping a newer version onto a fixture that lacks what that version requires would make the
fixture untrue.

**The reject set waits for the slowest host.** It is host-invariant (no case carries `appliesTo`),
so it may only name versions below the _lowest_ floor among registered hosts. Its other end is the
one exception: `manifest.reject.majorAhead` holds a full 2.0 manifest stamped one major above the
newest any registered host reads, which every reader refuses with an update message (`format.md`
§11), so it names a version above every floor. It is stamped `3.0.0` since the Revit host reads 2.x,
and is restamped one major ahead again when a host reads 3.x, never deleted. v18 and v19 could
join it only because both registered hosts take the 1.0.0 floor together; had one host repinned
first, the retired versions would have stayed out until the other caught up.
