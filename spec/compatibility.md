<!-- SPDX-License-Identifier: CC-BY-4.0 -->
<!-- SPDX-FileCopyrightText: Mantle Place LLC -->

# MPB compatibility policy

What a version number means, what a producer promises, how a reader tells the majors apart, and what a reader must do with a field or a version it has never seen.

Requirement words — MUST, MUST NOT, SHOULD, MAY — carry their [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119) meanings.

## 1. Telling the versions apart

**The majors are told apart by where the manifest is.** A reader looks for `Manifest.json` at the root of the zip first: that is a 3.x bundle ([`format.md`](format.md)). If it is absent, it looks for `Metadata/manifest.json`: that is a 2.x or 1.x bundle ([`format-2.md`](format-2.md)). A zip with neither is not a bundle. The place decides before the version is read, because the two majors hold different trees.

**Within a place, the manifest declares its own version,** and there are two eras:

| Era | `version` is | Published as |
| --- | --- | --- |
| **Integer pre-history** | a JSON **number** | `v<N>.json` |
| **MPB semver** | a JSON **string**, `MAJOR.MINOR.PATCH` | `<X.Y.Z>.json` — no `v` prefix |

⛔ **A consumer MUST distinguish the two by the JSON type, and MUST NOT coerce one into the other.** Parsing a semver string as a number yields zero, which sorts the newest contract below the oldest — a comparison that looks like a version gate and is the exact inverse of one. Comparing semver strings lexicographically is the same bug wearing different clothes: `"19"` sorts above `"1.0.0"` on the second character. Compare the *parsed major*, or compare the type first.

The integer era is closed. It is **Public pre-history**: those schemas stay published forever, frozen, and are documented in the [changelog](changelog.md), but they are not specified by this directory. Everything in `spec/` describes the semver era.

## 2. What each component means

- **MAJOR** — a breaking change. Files or fields removed, moved, renamed, retyped or narrowed. A consumer written for one major cannot be assumed to read another.
- **MINOR** — strictly additive. In 3.x: a new file in the tree, a new column in a table, a new optional fact in the manifest, a widened set of values. Nothing existing changes shape or meaning.
- **PATCH** — editorial. Documentation and description text only; no change to what validates. A patch release therefore obliges a host to nothing: no re-pin and no re-verification, because nothing a reader parses has changed.

⛔ **A field is never reused with a changed meaning,** and neither is a file name. A key or a name that meant one thing keeps meaning it, for as long as it exists. Repurposing one is the one change that no version number can warn a consumer about, because the document still validates and the values still parse. One exception has been published; the [changelog](changelog.md) records it, and what a reader must do about it.

## 3. What a consumer must do

⛔ **Ignore unknown fields, columns and files.** Additive is only additive if consumers tolerate it. A consumer that rejects a document for carrying a field it does not recognise, or a bundle for holding a file it does not know, turns every minor release into a breaking one.

⛔ **Refuse an unknown higher MAJOR gracefully.** A 3.x reader opens `Manifest.json` at the root, which keeps its name and place in every later major so that it can, reads `version`, and parses the major (§1). If that major is higher than any the reader supports, it stops before it extracts a file or imports part of the bundle. A clear refusal names the bundle's version, says which versions the reader supports, and tells the user what to do, which is usually to update the software. Attempting the import anyway is not correct behaviour; neither is a crash, nor a silent partial import. Refusal is a supported outcome of this spec, not a failure of it.

**A 2.x reader meeting a 3.0 bundle.** A reader written for 2.x looks for `Metadata/manifest.json`, which a 3.x bundle does not have, so it cannot read the version and refuse by it as the 2.x text described. A 2.x reader that finds `Manifest.json` at the root and no `Metadata/manifest.json` SHOULD read it as a newer major and refuse it as above, rather than reporting that the file is not a bundle. Mantle Place's own plugins are told to update by the platform before a 3.x bundle is downloaded to them.

**Read both majors.** A 2.x bundle is never repackaged (§8), so curators keep holding 2.x bundles after 3.0 ships. A reader written for 3.x SHOULD also read 2.x, telling them apart by where the manifest is (§1); Mantle Place's own plugins do. A third-party reader chooses its own floor (§5).

**Read a same-major manifest.** Within a major, a consumer reads what it recognises and ignores the rest — that is what makes minors safe to publish without a coordinated upgrade.

**Handle unknown enum values as the schema says to.** Each enum states its own behaviour. The informational ones — reasons, verdicts, labels a user reads — are echoed opaquely, because a consumer that branches on the values it knows shows nothing for the value it does not. The load-bearing ones — units, datums, axes, encodings, anything that scales or transforms geometry — **fail closed**, naming the value. There is no single rule for all enums, and the schema is where the per-enum answer lives.

## 4. Freeze-on-publish

⛔ **Every published version is immutable from the moment it is published.** There is no editable window at the newest version. A change of any kind ships as a new version: additive as a minor, editorial as a patch, breaking as a major.

This is stricter than it sounds, and it is stricter on purpose. Under the earlier rule a version froze only when superseded, which left the current version — the only one an external implementer would ever pin — as the one version allowed to change underneath them. Two edits legitimately rode that window before it was closed. A spec whose newest version can gain fields after publication is not a spec anyone can build against.

The mechanism is public and checkable: [`frozen.lock.json`](https://mantle.place/.well-known/schemas/bundle-manifest/frozen.lock.json), served beside the schemas, carries a sha256 of the exact bytes of **every** published version, the newest included. Hash the file you fetched and compare. A published schema whose bytes no longer match its ledger entry is a bug worth reporting, and CI on the producing side fails on it.

**A patch's promise is checked, not asserted.** §2 says a patch obliges a host to nothing, which is only safe if "editorial" is true rather than merely claimed. The consumer-side conformance gate implements it literally: a newer PATCH is exempt from the you-have-not-been-verified-against-this failure, and the exemption is earned per release — the gate fetches both published documents and requires them to be identical once prose, examples and each document's own version stamp are stripped. A release labelled a patch that changes what validates fails there instead of passing unread.

**Nothing is ever withdrawn.** Every version ever published stays served at its URL, pre-history included. Deprecation therefore never breaks a fetch: a consumer pinned to an old version keeps resolving its schema indefinitely. What changes is only whether new bundles are produced against it.

## 5. Version floors are policy, not spec

A consumer MAY decline to support versions below some floor of its own choosing. That is a product decision belonging to whoever ships the consumer, and **this spec does not impose one**.

Mantle Place's first-party plugins take a deliberately strict line — a short list of supported versions, declared in one place, with everything outside it refused and the user told to update the plugin or download the AOI again from their vault. That is our policy for our plugins. It is not an obligation this spec places on yours, and a third-party consumer that chooses to read several versions is conforming.

## 6. What this policy does not cover

**The vault REST API and the Platform API.** The service contract between our plugins and the Mantle Place platform, the level a host is recommended and the Extras beside a bundle included, is not part of the interchange format and is not versioned by it. A third-party consumer never needs it — reading a bundle from disk requires no account, no token and no network call.

**Extras.** The Site Brief, the plan sheets and the terrain tiles are downloads beside a bundle, outside the standard. They carry no version promise and can change, be added or be withdrawn without a new MPB version ([`format.md`](format.md) §1).

**A 2.x bundle's `platform` block, and its folder names for a consumer.** Both are 2.x matters, described in [`format-2.md`](format-2.md). A 3.x manifest has no register reserved for the platform, and in 3.x the names are the contract: every file's name and place are promised (§8).

## 7. A release is three things at once

A version of this spec is not the schema alone. It is the schema, this prose, and the conformance corpus — published together and versioned together, so that "conforming" is a claim a test can settle. [`conformance.md`](conformance.md) is where that is spelled out.

## 8. The 3.x promise

**Within 3.x nothing breaks.** A bundle written to any 3.x release is readable by a consumer written against any earlier 3.x release, and a producer written to 3.0 stays conforming for as long as 3.x lasts. Concretely, no 3.x release:

- removes, moves or renames a file of the tree, or a folder ([`format.md`](format.md) §1, §3);
- changes a file's format, its family, its unit rule or what its stored values mean ([`format.md`](format.md) §4, §6, §7);
- changes what one of the naming rules means ([`format.md`](format.md) §2);
- removes, moves, renames, retypes or narrows a fact the manifest states (§2), or gives a key a new meaning;
- adds a per-host block to the manifest;
- changes a published schema (§4).

A 3.x minor can add: a file that passes the admission rule ([`format.md`](format.md) §3), a column, a fact in the manifest, a value of a widened set, a record layer.

**What the promise does not cover.** Extras, the vault REST API and the Platform API (§6), and the measured size of a download.

**Bundles keep their version.** A bundle is made once, at the version current when it was built. A delivered bundle is never repackaged: nothing turns a 2.x bundle into a 3.x one, and a bundle that is already in a vault stays readable by a reader of its own version. A curator who wants a 3.x bundle orders one.

**The 2.x promise still holds for 2.x bundles.** Within 2.x nothing broke: every 2.x release only grew the 2.x manifest, its folder allowlist, its naming rules and its canonical list, as [`format-2.md`](format-2.md) §11 describes. 3.0 is the major that changes them, and it changes nothing in a bundle already delivered.
