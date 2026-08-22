# The shared conformance corpus

Language-neutral test vectors that **every** Mantle Place host plugin runs against its own parser.

**This corpus is normative.** It ships as one third of an MPB spec release — the schema, the prose
in [`spec/`](../../../spec/), and these vectors, published and versioned together — and it is the
part that decides: *a conforming reader passes the corpus*. The prose describes; the schema and this
corpus settle disagreements. See [`spec/conformance.md`](../../../spec/conformance.md) for what
claiming a group obliges you to, and which groups a third-party bundle reader needs at all.

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
restated here (`DOC-02`).

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

Fixtures are written against manifest **1.0.0**, and the readers' version floor is **1.0.0** too.
Everything below the floor is in the reject set: clean break, one supported version (`HPS-31`). The
floor and the pin are deliberately split — `index.json`'s `manifestVersion` is the pin, and
`verified-against.json` records each host's own.

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

**The reject set waits for the slowest host.** It is host-invariant (no case carries `appliesTo`),
so it may only name versions below the _lowest_ floor among registered hosts. v18 and v19 could
join it only because both registered hosts take the 1.0.0 floor together; had one host repinned
first, the retired versions would have stayed out until the other caught up.
