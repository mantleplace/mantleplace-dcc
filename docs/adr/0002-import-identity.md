---
status: accepted
---

# Imported content is keyed on the order, not the build that produced it

The Unreal importer groups everything it generates under one folder per import, and names its actors
with the same key. That key has been the pipeline job id, which changes on every rebuild of the same
order. Re-materialising an order therefore produced a *new* folder and a *new* set of actors, while
the idempotent wipe that exists to replace prior content looked at a path nobody was using any more —
so a user who refreshed an area got a second landscape sitting on top of the first, silently, with no
way to tell which was current. The key becomes the **order** identity, falling back to the bundle's
own content hash when a bundle carries no order (a locally produced or admin bundle).

This is not a naming preference. The job id is an identity for a *build*; the folder needs an
identity for a *thing the user owns*, and those are different domain concepts —
[`CONTEXT.md`](../../CONTEXT.md) now names them separately for exactly this reason. Using one where
the other belongs is what made re-import non-idempotent, and it presented as a cosmetic untidiness
rather than as the data problem it was.

## Considered options

- **Keep the job id and accept a folder per rebuild.** Rejected: it makes every refresh accumulate a
  full copy of the imported content, and leaves the user to work out by hand which landscape is live.
  It also makes the wipe permanently dead code, which is worse than either behaviour on purpose.
- **Order id when present, job id otherwise.** Rejected: the fallback is the local-development path,
  which is the one exercised most often during plugin work, so the bug would have stayed alive
  exactly where it is most likely to be hit and least likely to be believed.
- **A user-visible name from the area label.** Rejected: it is not an identity — two orders can carry
  the same label — and sanitising arbitrary text into a package path is its own hazard.

## Consequences

- The fallback puts a content hash fragment in a package path, which looks arbitrary to a reader who
  does not know why. That is the main reason this record exists.
- A rebuild of the same order now *replaces* prior content rather than adding to it. That is the
  intent, and it means a rebuild is destructive by design — so the identity a folder was created
  under is recorded alongside the content, and a mismatch refuses rather than deletes. Truncated
  identities can collide, and a collision that silently force-deleted a different order's content
  would be the worst outcome available.
- Content imported by 0.3.0 and earlier is keyed the old way and matches nothing. It is not migrated:
  the importer detects a folder with no recorded identity and says so, leaving the deletion to the
  user. Automatically deleting a user's content on a plugin upgrade is not a thing this plugin will
  do.
