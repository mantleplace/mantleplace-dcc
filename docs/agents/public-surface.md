---
name: public-surface
description: What may and may not be written into a world-readable repository — the refused citations (issue numbers, private repository names, internal decision-log ids), the five publication surfaces (files, commit messages, PR titles, PR bodies, branch names), and the structural exemptions. Read before writing a commit message, a PR title or body, a branch name, or any citation; enforced by the ci-public-hygiene gate.
---

# Everything here is public

Anything written into this repository lands in a world-readable place, permanently, whether or not it
is later deleted. That is the whole of the rule; the rest of this file is what it refuses, what it
lets through, and where it is checked.

Root [`CLAUDE.md`](../../CLAUDE.md) carries the trigger sentence. This file is the detail behind it.

## Cite only public URLs

The bundle-manifest schema series at
`https://mantle.place/.well-known/schemas/bundle-manifest/` is public and is the authority on the
contract. Two filename families are served: `v{N}.json` for the integer pre-history and
`{X.Y.Z}.json` for the MPB semver era — no `v` prefix, the `v` belonged to the integer era.
`frozen.lock.json` beside them names every version that exists.

Internal trackers, internal documents and internal repositories are not citable here — not by URL,
not by path, not by issue number.

## What is refused

The authority on this list is the module docstring of
[`tools/public-hygiene/check_public_references.py`](../../tools/public-hygiene/check_public_references.py).
It is reproduced here in prose, not restated as a second rulebook: where the two differ, the script
is right.

- **A bare `#NNN`.** In a Markdown file a bare `#42` auto-links to *this* repo's issue 42, which is
  worse than dangling: it is wrong and it looks deliberate. In a source comment it does not link and
  is still a citation of something no reader here can open. Both are refused; only the first is also
  misleading.
- **The qualified form, `repo#42`.** Forbidden the same, by explicit decision rather than by
  accident: it hands a stranger a private repository's name and a 404. The gate excepts this
  repository's own name.
- **A private sibling repository's name.** Refused structurally — this project's naming stem where
  the tail is not `dcc`. No private repository is named in the gate's own source; the only literals
  it carries are the tracked artifact filenames a legitimate commit message must be able to name.
- **A `D-` plus two capitals decision-log id.** It names a document in the private project
  repository that a reader cannot open, and unlike a rule id it is not a stable public identifier.
- **The shorthand shape** — a lowercase project short name immediately followed by a reference —
  because that is the shape that reached a real pull request body. Lowercase and followed by a
  reference, so prose about NAT traversal is untouched.

State the reasoning in prose instead of citing what a reader cannot open.

## Rule ids are fine, links to them are not

`HPS-40`, `DOC-06` and their kin are stable public identifiers and stay as prose. Do not turn them
into paths. This is the distinction the gate draws: the id is a name the repository is expected to
use, the link is a door a stranger cannot open.

## The five publication surfaces

A file is one surface of five. A commit message, a pull request title, a pull request body and a
branch name are all world-readable the moment they are pushed — the branch name before any review
exists — and a commit message can never be edited at all.

| Surface | Bare `#42` | Everything else above |
| --- | --- | --- |
| Tracked files | refused | refused |
| Commit messages | **allowed** | refused |
| Pull request titles | **allowed** | refused |
| Pull request bodies | **allowed** | refused |
| Branch names | refused | refused |

**The one split:** in a commit message or a pull request body a bare `#42` is this repository's
native way to cite its own issue, and GitHub appends one to every squash-merge subject — refusing it
would flag every merge on `main`. In a *file* it is not native, and it is refused.

**Branch names carry no issue number.** The convention is `type/short-description`. The gate's shape
rule is structural: a path segment that opens with digits is an issue number wearing a slash,
whatever tracker it came from — and so are the retry shapes `#88`, `issue-88` and `gh-88`. Version
digits inside a word (`ue5-8`, `net10`, `mpb-1.0.0`) stay legal. The cross-reference to a private
tracker lives in the private side's pin-bump pull request, never here.

## The structural exemptions

Exemptions are structural rather than a list of blessed strings, so that a legitimate construct
keeps working as the tree grows and an illegitimate one cannot be waved through by adding a line to
a file nobody rereads. Each is anchored to the characters immediately before the match, so "looks
like an exemption somewhere on the line" cannot launder a real violation.

- **Markdown link targets** — `](#anchor)`, same-document or cross-file. They resolve for every
  reader, inside this repository.
- **Code spans**, fenced or inline. This is how the rule documents *itself*, and a gate that fails
  its own rulebook is a gate that gets deleted.
- **`host #2`** — an ordinal in prose. Host #2 is a count of DCC hosts, not a tracker.
- **Hex colours** — three or six hex digits after a colon is a hex triplet.
- **HTML numeric character references** such as `&#39;`. This tree serves a sign-in page, so they
  appear in the string literals that build its markup.

Exactly one path is exempt, named explicitly rather than matched by a pattern so the exemption
cannot quietly widen: the gate's own test corpus. A gate for forbidden strings has to contain
forbidden strings, the same way a spam filter's fixtures contain spam. The gate's own script is
deliberately not on that list.

## No credentials, ever, including in binary assets

`.uasset` files serialize property values, so a URL typed into any asset's defaults is *in the file*,
unreadable in review and permanent in history. The auth path no longer has a capture-sensitive value
to misplace — every route it uses is a public one compiled in — but anything of that kind is hydrated
at packaging time from the build's secret store and must never be set in a committed asset.

## This one is checked

`ci-public-hygiene` runs the gate, and its `references` job is a **required check on `main`**. It
became a gate after being prose alone until a private tracker's issue number reached a committed test
comment and sat on `main` — a rule nothing checks is a rule that decays silently and is noticed by a
stranger rather than by us.

CI runs *after* a push has already published. Opt into the pre-publication hooks once per clone:

```bash
git config core.hooksPath .githooks
```
