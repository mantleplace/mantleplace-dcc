# The shared conformance corpus

Language-neutral test vectors that **every** Mantle Place host plugin runs against its own parser.

This is procedural — the binding rules are the Host Plugin Standard's (`HPS-40`, `HPS-41`).

## Why it exists

The Unreal plugin had all of this already, as inline C++ string literals inside
`MantlePlaceImportManifestTest.cpp` and friends. That is a perfectly good test suite and a
completely unusable specification: a .NET or Python host could read the vectors only by reading
C++, and would inevitably re-derive them slightly differently. The bugs that follow — a `null`
sha256 read as "corrupt" instead of "unknown", a `+` left unescaped in a PKCE verifier, a UTM false
northing dropped in the southern hemisphere — are all silent. They produce a plugin that works on
the developer's bundle and misplaces a customer's site by kilometres.

Lifting the vectors here makes them one artifact with one owner, and makes "host #2 agrees with
host #1" a thing a test can assert instead of a thing a reviewer hopes.

**The Unreal suite now reads these files at run time rather than duplicating them** — six automation
tests across both plugin modules. Editing a case here turns those tests red, which is the only proof
that the corpus and host #1 have not quietly diverged. The `digest` group is the sharpest
demonstration: one edit to `sha256-vectors.json` fails two independent SHA-256 implementations
inside the same host.

## Using it from a host suite

Read `index.json`, iterate `cases`, and dispatch on `group` + `expect`:

| `expect` | What the host asserts |
| --- | --- |
| `accept` | the parser accepts `file` and produces every value in `expectations` |
| `reject` | the parser **refuses** `file`; if `errorContains` is present the message contains it. A reject case may still carry `expectations` — values the parser must have read *before* refusing (`HPS-37`) |
| `vector` | `file` is a known-answer table, not a document to parse — drive it row by row |

A case with `appliesTo` asserts one host's manifest block; every other host skips it (`HPS-41`).

Numeric tolerances are stated in the case (`landscapeSpawnToleranceCm`, `toleranceMetres`); do not
invent tighter ones, and do not compare floats exactly.

A host does not have to consume every group on day one — a manifest-first build order starts
with `manifest` and adds `vault`/`auth` as those layers land. It does have to consume
every case *in the groups it claims*, and `HPS-41` is what says so.

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
the two, and `check_manifest_conformance.py` verifies the set is *well-formed-broken* — each fixture
wrong in exactly its declared way. Expectation keys prefixed `selfTest` are a reserved namespace no
host may ever consume.

## Provenance

| Group | Lifted from |
| --- | --- |
| `manifest` | `MantlePlaceImportManifestTest.cpp` (accept shape from the platform's published schema and its reference fixture) |
| `vault` | `MantlePlaceVaultLogicTest.cpp` |
| `auth` | `MantlePlaceAuthLogicTest.cpp` (RFC 7636 Appendix B, RFC 4648 §5) |
| `cache` | `MantlePlaceBundleCacheLogicTest.cpp` |
| `digest` | `MantlePlaceSha256Test.cpp` + the cache suite (NIST FIPS 180-4 known answers) |
| `projection` | `MantlePlaceRoadSplinesLogicTest.cpp` (`mesh.origin` of the reference fixture) |

"Lifted from" means the *assertions* came from those files, not that every fixture is byte-identical
to its C++ literal. Where a vector was renamed or padded for readability (placeholder sha values,
clearer decoy paths) the behaviour it exercises is unchanged; `manifest/full.json` is the one
that is byte-exact, because its derived expectations depend on the exact numbers.

Note the `projection` and `manifest` groups describe **different AOIs** — the projection pair comes
from `mesh.origin`, the manifest fixture from `unreal.georeference.origin`. They are not expected to
agree, and the case file says so.

## Version

Fixtures are written against manifest **v19**; the readers' version floor is **v18**. Everything
below the floor is in the reject set: clean break, one supported version (`HPS-31`). The floor and
the pin are deliberately split — `index.json`'s `manifestVersion` is the pin, and
`verified-against.json` records each host's own.

Case ids and filenames are **version-agnostic** on purpose (`manifest.full`, not
`manifest.v19.full`). Only the explicit version-gate rejects carry a number. A version bump is then
three moves — new accept shape, previous version to the reject set, `manifestVersion` repinned —
rather than a rename sweep across every case a host suite references.
