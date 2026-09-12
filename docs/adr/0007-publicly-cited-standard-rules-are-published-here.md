---
name: adr-0007-publicly-cited-standard-rules-are-published-here
description: Every `HPS-NN` rule id cited in a public file must resolve in a public document, so the publicly-cited half of the Host Plugin Standard is published in this repository and rules that stay private become uncitable in public. Read before adding an `HPS-NN` citation to any file here, and before assuming the ids are internal shorthand.
status: accepted
---

# The publicly-cited half of the Host Plugin Standard is published here

The Host Plugin Standard is the cross-host normative rulebook both plugins implement, cited
throughout this repository by `HPS-NN` id. Its master is private. Its citations are not.

Measured on the tree at the time of this record:

| | |
| --- | --- |
| `HPS-NN` occurrences in tracked files | 641 |
| Distinct ids cited | 48 |
| Files carrying at least one | 125 |

Two of those files are `README.md`s written for strangers. A reader who arrives at the Revit host's
README to decide whether to trust the plugin is told that sign-in opens the system browser and never
an embedded webview, that the refresh token is stored per-OS-user, and that a download is verified
before it is renamed into place — each claim followed by an identifier that resolves nowhere they
can reach.

## The rule this repository already had, and the hole in it

[`docs/agents/public-surface.md`](../agents/public-surface.md) states the general form: *state the
reasoning in prose instead of citing what a reader cannot open*. It then carves rule ids out of that
rule, on the grounds that an id is a stable public identifier rather than a door a stranger cannot
open.

That carve-out was right about the ids and wrong about the consequence. An identifier is only stable
*and* useful if something resolves it. Left alone, the carve-out grows: it is the one exemption that
does not bottom out in something a reader can reach, and 641 citations is what one exemption
compounds to.

## Decision

**A public file may cite only a rule whose text is public.**

- The publicly-cited half of the standard is published in this repository, as a document with the
  same shape and the same rule ids as the private master. The master stays where it is; the public
  document is a faithful subset, not a fork, and the master remains the authority where the two
  could ever differ.
- Rules that must stay private stay private, and become **uncitable in public** as a direct result.
  That is the correct pressure and not a loss: a rule whose reasoning cannot be published is a rule
  whose public citation was never explaining anything.
- The rule is enforced rather than trusted. The hygiene gate gains a check that extracts every
  `HPS-NN` from a public file and fails when it does not resolve in the published document. A rule
  nothing checks is a rule that decays silently, which is how this repository's hygiene rule became
  a gate in the first place.
- **Until the document exists, no public file may add an `HPS-NN` citation it does not already
  carry.** This stops the count growing while the work is in flight.

## What was rejected

- **Strip the ids from public prose and state the reasoning in words.** Cheaper, and it deletes real
  information. The ids are how the two hosts' implementations are tied to one contract, and a reader
  comparing the Revit and Unreal READMEs can currently see that both cite the same rule for the same
  behaviour. Prose loses that.
- **Leave it, and say once in the public README that the ids are internal.** Honest, and it leaves
  the repository unable to explain itself. Open source where the reasoning resolves only inside a
  private repository is open in the way that matters least.
- **Publish the standard in full.** Refused by the boundary law in root
  [`CLAUDE.md`](../../CLAUDE.md). Some of these rules are exactly the logic whose capture by a fork
  would hurt Mantle Place.

## Consequences

- **`spec/` is not the home.** Its charter is the bundle format, and it says in as many words that
  it never restates anything else. Host sign-in and cache integrity are not the format. The
  precedent to follow is [`docs/platform-auth-contract.md`](../platform-auth-contract.md), which is
  already the one contract both hosts implement, published here, in this repository's `docs/`.
- **That precedent is also an overlap to resolve.** `platform-auth-contract.md` already covers
  sign-in, tokens and refresh, which is the same ground as a block of the rules now to be published.
  Whether the published standard supersedes that file, defers to it, or cites it is a decision for
  the work itself and is not settled here.
- **Publication is permanent.** Anything published under this record is world-readable forever,
  whether or not it is later deleted. The rule-by-rule judgment of what is portable is therefore
  made once, deliberately, against the boundary law — not in the tail of a session.
