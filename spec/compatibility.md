# MPB compatibility policy

What a version number means, what a producer promises, and what a consumer must do with a field or
a version it has never seen.

Requirement words — MUST, MUST NOT, SHOULD, MAY — carry their
[RFC 2119](https://www.rfc-editor.org/rfc/rfc2119) meanings.

## 1. Two version families, told apart by JSON type

The manifest declares its own version, and there are two eras:

| Era | `version` is | Published as |
| --- | --- | --- |
| **Integer pre-history** | a JSON **number** | `v<N>.json` |
| **MPB semver** | a JSON **string**, `MAJOR.MINOR.PATCH` | `<X.Y.Z>.json` — no `v` prefix |

⛔ **A consumer MUST distinguish the two by the JSON type, and MUST NOT coerce one into the other.**
Parsing a semver string as a number yields zero, which sorts the newest contract below the oldest —
a comparison that looks like a version gate and is the exact inverse of one. Comparing semver
strings lexicographically is the same bug wearing different clothes: `"19"` sorts above `"1.0.0"` on
the second character. Compare the *parsed major*, or compare the type first.

The integer era is closed. It is **Public pre-history**: those schemas stay published forever,
frozen, and are documented in the [changelog](changelog.md), but they are not specified by this
directory. Everything in `spec/` describes the semver era.

## 2. What each component means

- **MAJOR** — a breaking change. Fields removed, moved, renamed, retyped, or narrowed. A consumer
  written for one major cannot be assumed to read another.
- **MINOR** — strictly additive. New optional fields, new blocks, a new `hosts.<hostId>` sub-block,
  a widened enum. Nothing existing changes shape or meaning.
- **PATCH** — editorial. Documentation and description text only; no change to what validates.
  A patch release therefore obliges a host to nothing: no re-pin and no re-verification,
  because nothing a reader parses has changed.

⛔ **A field is never reused with a changed meaning.** A key that meant one thing keeps meaning it,
for as long as it exists. Repurposing a key is the one change that no version number can warn a
consumer about, because the document still validates and the values still parse.

## 3. What a consumer must do

⛔ **Ignore unknown fields.** Additive is only additive if consumers tolerate it. `additionalProperties`
is open throughout the schema precisely so that a newer producer's optional field flows through an
older consumer untouched. A consumer that rejects a document for carrying a field it does not
recognise turns every minor release into a breaking one.

⛔ **Refuse an unknown higher MAJOR gracefully.** A clear refusal that names the version and tells
the user what to do is correct behaviour. Attempting the import anyway is not; neither is a crash,
nor a silent partial import. Refusal is a supported outcome of this spec, not a failure of it.

**Read a same-major manifest.** Within a major, a consumer reads what it recognises and ignores the
rest — that is what makes minors safe to publish without a coordinated upgrade.

**Handle unknown enum values as the schema says to.** Each enum states its own behaviour. The
informational ones — reasons, verdicts, labels a user reads — are echoed opaquely, because a
consumer that branches on the values it knows shows nothing for the value it does not. The
load-bearing ones — units, encodings, anything that scales or transforms geometry — **fail closed**,
naming the value. There is no single rule for all enums, and the schema is where the per-enum
answer lives.

## 4. Freeze-on-publish

⛔ **Every published version is immutable from the moment it is published.** There is no editable
window at the newest version. A change of any kind ships as a new version: additive as a minor,
editorial as a patch, breaking as a major.

This is stricter than it sounds, and it is stricter on purpose. Under the earlier rule a version
froze only when superseded, which left the current version — the only one an external implementer
would ever pin — as the one version allowed to change underneath them. Two edits legitimately rode
that window before it was closed. A spec whose newest version can gain fields after publication is
not a spec anyone can build against.

The mechanism is public and checkable:
[`frozen.lock.json`](https://mantle.place/.well-known/schemas/bundle-manifest/frozen.lock.json),
served beside the schemas, carries a sha256 of the exact bytes of **every** published version, the
newest included. Hash the file you fetched and compare. A published schema whose bytes no longer match its ledger entry is a bug worth
reporting, and CI on the producing side fails on it.

**Nothing is ever withdrawn.** Every version ever published stays served at its URL, pre-history
included. Deprecation therefore never breaks a fetch: a consumer pinned to an old version keeps
resolving its schema indefinitely. What changes is only whether new bundles are produced against it.

## 5. Version floors are policy, not spec

A consumer MAY decline to support versions below some floor of its own choosing. That is a product
decision belonging to whoever ships the consumer, and **this spec does not impose one**.

Our own first-party plugins take a deliberately strict line — a single supported version, declared
in one place, with everything below it refused and the user told to re-download the AOI from their
vault. Re-procurement, not dual-parsing, is how we handle old bundles, because a fallback ladder is
a second parser that nobody tests. That is our policy for our plugins. It is not an obligation this
spec places on yours, and a third-party consumer that chooses to read several versions is
conforming.

The same goes for the advisory host-version floors published inside a host block. They record the
minimum host application version the platform has actually built against. A spec that mandated
floors would be legislating other people's release trains.

## 6. What this policy does not cover

**The vault REST API.** The service contract between our plugins and the Mantle Place vault is not
part of the interchange format and is not versioned by it. It is documented as SDK material. A
third-party consumer never needs it — reading a bundle from disk requires no account, no token and
no network call.

**The `platform` block.** Explicitly non-normative: reserved, present, and outside the contract. Its
inner shape may change in a minor release, which no consumer may depend on. See
[`format.md` §4.3](format.md#43-platform--present-reserved-and-not-part-of-the-contract).

**Folder names.** Human-facing convention, free to evolve. The machine contract is the pointer
values ([`format.md` §3](format.md#3-the-pointer-doctrine)).

## 7. A release is three things at once

A version of this spec is not the schema alone. It is the schema, this prose, and the conformance
corpus — published together and versioned together, so that "conforming" is a claim a test can
settle. [`conformance.md`](conformance.md) is where that is spelled out.
