# The MPB format

How a Mantle Place Bundle is put together, and how a consumer is meant to read it.

The normative shape of the manifest is the published JSON Schema, cited throughout by block name
rather than restated: `https://mantle.place/.well-known/schemas/bundle-manifest/`.
This document explains what the blocks are *for* and which of them a reader is obliged to honour.

Requirement words — MUST, MUST NOT, SHOULD, MAY — carry their
[RFC 2119](https://www.rfc-editor.org/rfc/rfc2119) meanings. Every MUST below is decided by the
schema, by the conformance corpus, or by both; where a corpus group settles the question, this
document says which one.

## 1. A bundle is a zip with one human file and one machine entry point

```
<bundle>.zip
  README.md                 the only top-level file — written for a person
  Metadata/manifest.json    the machine entry point — everything else is reached from here
  <Modality>/…              the artifacts
```

Everything else lives under a modality folder. The README is generated per bundle and is not part of
the contract; a consumer MUST NOT parse it.

A **sidecar manifest** is also published beside the zip in the vault, carrying the same document
plus one extra block — see [§7](#7-the-sidecar-manifest). Inside the zip,
`Metadata/manifest.json` is the manifest.

## 2. Modality folders are human-facing, and machines never navigate by them

The top-level folders are one-word nouns naming a **modality**, not a tool: `Basemap`, `Imagery`,
`Elevation`, `Surface`, `Site`, `Mesh`, `Vector`, `Landcover`, `Flood`, `Metadata`. The vocabulary
is a frozen allowlist on the producing side — growing it is a deliberate, reviewed act — and it is
frozen so that a curator opening the zip finds the same words next year.

The naming rule inside a folder: **basename is artifact identity, extension is format.**
`Surface.dxf` and `Surface.landxml` are one artifact in two formats. A compound basename appears
only where a file must still describe itself after being copied out of its folder.

**None of this is a machine contract.** Folder names are human-facing convention and are free to
evolve; they have already been reorganised once, and a pointer-driven consumer needed no change to
survive it. Which brings us to the rule that matters most in this document.

## 3. The pointer doctrine

⛔ **A consumer MUST locate every file through a manifest pointer VALUE, and MUST NOT construct a
path from folder convention.**

Two kinds of pointer carry every file in a bundle:

- the **`layout` block**, whose keys name artifacts and whose values are archive-relative paths; and
- **per-artifact `path` fields** inside the blocks that describe an artifact in detail.

The keys are stable; the values move. A pointer is present when its artifact shipped, and some layer
pointers are explicitly null when their layer is absent — the schema states which behaviour each
pointer has. Reading a pointer value and extracting exactly that zip entry is the whole procedure.

**The manifest itself is the one exception**, of necessity: `Metadata/manifest.json` is found by
convention, because a reader has to open it before it can read a pointer to anything. It also
publishes a pointer to itself, which is how a consumer that already holds the parsed document can
name the entry it came from. Every *other* file in the bundle is found by pointer.

The failure this prevents is not hypothetical, and it is not loud. A consumer that hardcodes a
well-known relative path keeps working until the producer reorganises the archive, and then fails on
a customer's bundle rather than in anyone's CI.

**Archive paths are untrusted input.** A consumer MUST resolve every pointer inside its extraction
root and reject entries that escape it. The producer does not emit such paths; a reader that assumes
so is one malformed zip away from writing outside its own directory.

## 4. The manifest has three registers

One JSON document — but its top-level blocks divide by *who they are for*, and a reader's
obligations differ by register.

### 4.1 The neutral core — the bundle's own facts

Host-independent truth about what was delivered: the AOI bounds, the build's identity and
timestamps, the delivery CRS and unit system, the packaging contract this bundle was produced
against, the completeness verdict, a block per modality describing the artifacts that shipped,
licensing, attribution, and pipeline provenance.

Every consumer reads this register. It is the half that answers *what is in this bundle, and under
what terms*.

### 4.2 `hosts.<hostId>` — pre-derived placement, one subtree per host

Everything host-specific lives under `hosts.<hostId>`, and a consumer targeting a host reads
**exactly that subtree**.

⛔ **A consumer MUST NOT read another host's block**, and MUST NOT merge two. The blocks are
deliberately not shaped alike: one host wants a flat projected frame in metric UTM, another wants a
survey point in the delivery CRS. A reader that takes whichever block it finds first and treats it
as "the georeference" will report a plausible CRS and place a site in the wrong country, with
nothing failing. The corpus pins this with a case carrying two conflicting host georeferences on
purpose.

A host sub-block always carries its **readiness** verdicts (§6.1). It does not always carry a
payload: a bundle whose artifacts for that host have not been materialised yet is readiness-only,
and that is a well-formed manifest, not an error.

New hosts join additively — a new sub-block in a minor release. A consumer MUST ignore host ids it
does not recognise, and MUST NOT infer anything from their absence.

### 4.3 `platform` — present, reserved, and not part of the contract

Non-normative. It carries values the Mantle Place web application reads instead of hard-coding them.

⛔ **No consumer may depend on the `platform` block.** Its inner shape may change in a minor
release, which is exactly what "not part of the interchange contract" buys. It is documented here so
that a reader who encounters it knows it is deliberate rather than an undocumented leak.

## 5. Integrity

Artifacts carry a lowercase-hex sha256 of their exact bytes. The rules for using it are short, and
the interesting half is what to do when a hash is absent.

⛔ **Where the schema requires a hash, a consumer MUST verify it and MUST fail closed on a
mismatch** — refuse the import, and name the artifact. Importing bytes that failed verification is
worse than importing nothing, because the failure surfaces later, as data that looks real.

⛔ **A missing optional hash means _unknown_, not _corrupt_.** Where the schema does not require a
hash and none is present, the check is skipped and the artifact remains valid. A consumer SHOULD
report that distinction rather than claim a verification it did not perform. Treating unknown as
corrupt makes every older bundle un-openable; treating it as verified is a lie. A hash that IS
required and is absent is a producer bug and a refusal — the two cases are told apart by the schema,
never by guesswork.

Where an artifact carries no hash of its own, a consumer resolves one by matching its path against
the manifest's own format tables rather than concluding there is none.

Downloading is outside this spec, but one adjacent practice is worth stating because getting it
wrong corrupts a cache silently: write a download to a temporary path, verify it, and promote it by
rename. A consumer that streams onto the final path leaves a truncated file that looks cached, and
imports it on the next run.

Corpus groups `digest` (NIST FIPS 180-4 known answers, including the streaming-equivalence cases)
and `cache` are the executable form of this section.

## 6. Placement, coordinates and units

⛔ **Placement values are pre-derived by the platform and MUST be applied verbatim. A consumer MUST
NOT re-derive them.** Scales, offsets, origins, extents, rotations, quantisation mappings — all of
it is computed once, upstream, and published. The consumer multiplies published numbers into its own
coordinate convention and does nothing else.

This is the most important rule in the spec, and it is a rule rather than advice because the failure
mode is a bundle that imports successfully and is wrong. A consumer that recomputes a transform "to
be safe" has built a second implementation of a pipeline it cannot see, and the two drift in the
field rather than in CI. The corpus carries the expected derived values with explicit tolerances;
compare against those, and never against exact floats.

Two consequences are worth spelling out, because both have bitten:

- **Grid rotation is identity by construction.** The producing emitters reproject per vertex, so
  meridian convergence is already absorbed into the coordinates. A consumer that assumes rotation
  must be derived will derive it, and be wrong by metres per kilometre.
- **The units question has more than one answer in the same bundle, deliberately.** The bundle-level
  unit system, the delivery tier and its linear unit, and each artifact's own units field describe
  different scopes, and they can legitimately disagree — a delivery whose region has no projected
  foot zone ships an origin stated in one unit beside artifacts stated in another, which is exactly
  why the unit is published next to the coordinates it describes instead of being inferred once per
  bundle. **Read the unit from the thing you are placing.** The schema is the authority on which
  field describes which scope.

⛔ **A consumer MUST fail closed on a unit or an enum value it does not understand**, naming the
offending value. A linear unit misread by a factor is a site misplaced by a whole ratio, and a
silent fallback to a default is how that happens. The schema states unknown-value behaviour per
enum: some are informational and are echoed opaquely, others are load-bearing and fail closed.

⛔ **Where the manifest states a value twice over, a consumer MUST verify the identity and refuse on
mismatch** rather than picking one. Those redundancies are published where a host's own API demands
internal consistency; they exist to be checked.

**Local projection is permitted in exactly one narrow case.** Where a bundle ships geometry whose
own coordinates are geographic — a vector layer in lon/lat, which the manifest describes as a layer
rather than vertex by vertex — a consumer must project in order to place it, and MAY do so. That is
the only projection a consumer performs; it is not licence to carry a geoprocessing stack. A
consumer that does project MUST match the corpus `projection` group's known answers, southern-
hemisphere false northing included. Getting a zone wrong places geometry kilometres away while every
test that does not check numbers still passes.

### 6.1 Honest absence

Three parts of the manifest exist so that a consumer can explain a missing artifact instead of
dead-ending on an empty import:

- **`packaging`** — the format contract this bundle was produced against: what was requested, what
  was delivered, and one honest row per requested-but-absent item saying why.
- **`completeness`** — the bundle's own verdict against its must-ship set.
- **`hosts.<hostId>.readiness`** — per-import-path verdicts for that host: present or not, and when
  not, the stated reason.

⛔ **A consumer MUST surface the stated reason rather than matching on it.** The reason vocabulary
is the schema's to grow; a consumer that branches on the values it knows shows a blank dialog for
the value it does not. Echo it.

An absent artifact is not an error. "Not produced", "not selected" and "available on request" are
three different sentences to show a user, and the manifest is what distinguishes them.

### 6.2 Is this bundle usable at all

⛔ **A consumer MUST decide "has this bundle been materialised" from the manifest's neutral signals,
not from the presence of its own host block.** The signals are host-agnostic — a non-empty `hosts`
object, or a non-empty set of vector layers — and any one of them is sufficient. Note what that
means: a bundle materialised for a host this consumer has never heard of still reads as
materialised, which is the point. Such a bundle is a well-formed manifest that the consumer parses
and then declines to *import*, with guidance — not an invalid document, and not an unmaterialised
one. The corpus decides this one, row by row.

⛔ **A bundle with no host block at all MUST still parse.** The top-level facts — bounds, layout
pointers, packaging, the order identity — are readable and MUST be read before returning "not
importable", so that a caller can still identify the bundle it is holding.

Two identifiers appear in the manifest and are **not interchangeable**: the per-rebuild job identity
and the order identity. Only the order identity joins a bundle to its vault entry.

## 7. The sidecar manifest

The vault publishes a versioned copy of the manifest beside the zip, so that a listing can show a
bundle's version, size and digest without downloading and unzipping it.

The sidecar is the same document plus one block — `bundle`, carrying the zip's own sha256, size and
filename. **That block is sidecar-only.** It never appears inside the zip's own
`Metadata/manifest.json`; a consumer MUST NOT require it, and MUST NOT read its absence from an
in-zip manifest as an error.

## 8. Reading a bundle, start to finish

The whole procedure, for a consumer holding a zip:

1. **Extract and parse `Metadata/manifest.json`.**
2. **Check the version.** Its JSON type tells you the era — a number is the integer pre-history, a
   string is MPB semver. Compare the major component, and refuse an unknown higher major gracefully
   ([`compatibility.md`](compatibility.md)).
3. **Decide materialisation** from the neutral signals (§6.2). If the bundle carries no payload for
   your host, read the top-level facts, then report the readiness reasons rather than an empty
   import.
4. **Locate each file** by reading its `layout` pointer or its per-artifact `path` and extracting
   exactly that entry, resolved inside your extraction root (§3).
5. **Verify integrity** per §5 — fail closed where the schema requires a hash; skip, and say so,
   where it does not.
6. **Place the geometry** by applying your host block's published values verbatim (§6), reading each
   unit from the field that describes the thing you are placing.
7. **Honour the licensing obligations.** The `licensing` and `attribution` blocks state which
   families carry which terms; the platform's own [licensing](https://mantle.place/licensing) and
   [attributions](https://mantle.place/attributions) pages are the authority on the text those terms
   require, and nothing in this repository restates them.

If a step above is under-specified for a bundle you hold, that is a spec bug worth an issue. If it
is under-specified in a way the corpus does not catch, it is a corpus bug too — see
[`conformance.md`](conformance.md).
